using System.Security.Cryptography;
using System.Text.Json;
using Dollhouse.Core.Security;
using Dollhouse.Server.Accounts;
using Dollhouse.Server.Licensing;
using Dollhouse.Server.Modules;
using Dollhouse.Server.Requests;

namespace Dollhouse.Server.Endpoints;

public sealed class LicenseLeaseEndpoint(LocalAccountStore accounts, LicenseChallengeStore challenges,
    LocalLicenseAuthority authority, TimeProvider? clock = null)
{
    public const string Path = "/api/v1/license/lease";
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromDays(7);
    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    public ServerRoute Route => new("POST", Path, HandleAsync);
    private static Task ErrorAsync(HttpContext context, int status, string error) =>
        JsonResponse.WriteAsync(context, status, new { Error = error });

    private async Task HandleAsync(ServerRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Body.Length > 8192) { await ErrorAsync(request.Context, 413, "LEASE_REQUEST_TOO_LARGE"); return; }
        var login = LocalSession.Resolve(request.Context.Request.Headers, accounts);
        if (login is null) { await ErrorAsync(request.Context, 401, "LOGIN_REQUIRED"); return; }
        var devices = request.Context.Request.Headers["X-Rehost-Device"];
        var headerDevice = devices.Count == 1 ? devices[0] : null;
        if (headerDevice is null || headerDevice.Length != 64 || headerDevice.Any(c => !char.IsAsciiHexDigitLower(c)))
        { await ErrorAsync(request.Context, 400, "INVALID_DEVICE"); return; }
        string publicKey, action, nonce, signature, hardware;
        try
        {
            using var json = RequestJson.ParseObject(request.Body);
            var root = json.RootElement;
            publicKey = RequestJson.String(root, "public_key");
            action = RequestJson.String(root, "action");
            nonce = RequestJson.String(root, "nonce");
            signature = RequestJson.String(root, "signature");
            hardware = RequestJson.String(root, "hw");
            if (publicKey.Length > 256 || signature.Length is < 1 or > 128 || action.Length is < 1 or > 64
                || nonce.Length != 64 || nonce.Any(c => !char.IsAsciiHexDigitLower(c))) throw new JsonException();
        }
        catch (JsonException) { await ErrorAsync(request.Context, 400, "INVALID_LEASE_REQUEST"); return; }
        if (action != "activate") { await ErrorAsync(request.Context, 400, "UNSUPPORTED_ACTION"); return; }
        if (!challenges.TryGet(nonce, out var challenge))
        { await ErrorAsync(request.Context, 400, "INVALID_CHALLENGE"); return; }
        var binding = challenge.Binding;
        try
        {
            var key = LicenseDeviceKey.Import(publicKey);
            if (binding.AccountId != login.User.Id || binding.Session != login.Session || binding.HeaderDevice != headerDevice
                || hardware != headerDevice || binding.PublicKey != key.PublicKey || binding.Device != key.Fingerprint
                || binding.Action != action || binding.Product != "fallendoll")
            { await ErrorAsync(request.Context, 400, "INVALID_CHALLENGE"); return; }
            var signatureBytes = LocalLicenseAuthority.DecodeBase64Url(signature);
            if (!key.Verify(LicenseProof.FromChallenge(challenge).Encode(), signatureBytes))
            { await ErrorAsync(request.Context, 400, "BAD_DEVICE_PROOF"); return; }
        }
        catch (Exception error) when (error is FormatException or CryptographicException or ArgumentException)
        { await ErrorAsync(request.Context, 400, "BAD_DEVICE_PROOF"); return; }

        var now = clock.GetUtcNow().ToUnixTimeSeconds();
        var expires = Math.Min(now + (long)LeaseDuration.TotalSeconds, authority.SigningNotAfter - 1);
        if (now < authority.SigningNotBefore || expires <= now)
        { await ErrorAsync(request.Context, 503, "SIGNING_KEY_UNAVAILABLE"); return; }
        var envelope = authority.SignLease(new
        {
            Type = "lease", Product = binding.Product, KeyId = LocalLicenseAuthority.SigningKeyId,
            AccountId = binding.AccountId, DeviceId = binding.Device,
            IssuedAt = now, Aud = LocalLicenseAuthority.SigningKeyId, ReqNonce = nonce, binding.ClientNonce,
            Ver = 1, Entitlement = "lifetime", ExpiresAt = expires, SubExpiresAt = (long?)null, TrialLeft = 0
        }, now);
        // Device proof and signing succeeded; only one concurrent caller can consume and receive a lease.
        if (!challenges.TryConsume(nonce, binding, out _))
        { await ErrorAsync(request.Context, 400, "INVALID_CHALLENGE"); return; }
        await JsonResponse.WriteAsync(request.Context, 200, envelope);
    }
}
