using System.Security.Cryptography;
using System.Text.Json;
using Dollhouse.Core.Security;
using Dollhouse.Server.Accounts;
using Dollhouse.Server.Licensing;
using Dollhouse.Server.Modules;
using Dollhouse.Server.Requests;

namespace Dollhouse.Server.Endpoints;

public sealed class LicenseChallengeEndpoint(LocalAccountStore accounts, LicenseChallengeStore challenges)
{
    public const string Path = "/api/v1/license/challenge";
    public ServerRoute Route => new("POST", Path, HandleAsync);

    private static Task ErrorAsync(HttpContext context, int status, string error) =>
        JsonResponse.WriteAsync(context, status, new { Error = error });

    private async Task HandleAsync(ServerRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Body.Length > 4096) { await ErrorAsync(request.Context, 413, "CHALLENGE_REQUEST_TOO_LARGE"); return; }
        var login = LocalSession.Resolve(request.Context.Request.Headers, accounts);
        if (login is null) { await ErrorAsync(request.Context, 401, "LOGIN_REQUIRED"); return; }
        var deviceValues = request.Context.Request.Headers["X-Rehost-Device"];
        var device = deviceValues.Count == 1 ? deviceValues[0] : null;
        if (device is null || device.Length != 64 || device.Any(value => !char.IsAsciiHexDigitLower(value)))
        { await ErrorAsync(request.Context, 400, "INVALID_DEVICE"); return; }

        string publicKey, action, clientNonce, licenseDevice;
        try
        {
            using var json = RequestJson.ParseObject(request.Body);
            var root = json.RootElement;
            publicKey = RequestJson.String(root, "public_key");
            action = RequestJson.String(root, "action");
            clientNonce = RequestJson.String(root, "client_nonce");
            if (publicKey.Length is < 1 or > 256 || action.Length is < 1 or > 64
                || action.Any(value => !char.IsAsciiLetterOrDigit(value) && value is not '-' and not '_')
                || clientNonce.Length != 48 || clientNonce.Any(value => !char.IsAsciiHexDigitLower(value)))
                throw new JsonException();
            var key = LicenseDeviceKey.Import(publicKey);
            publicKey = key.PublicKey;
            licenseDevice = key.Fingerprint;
        }
        catch (Exception error) when (error is JsonException or FormatException or CryptographicException or ArgumentException)
        {
            await ErrorAsync(request.Context, 400, "INVALID_CHALLENGE_REQUEST");
            return;
        }

        var binding = new LicenseChallengeBinding(login.User.Id, login.Session, device, licenseDevice, publicKey, action, clientNonce);
        var challenge = challenges.Issue(binding);
        if (challenge is null) { await ErrorAsync(request.Context, 503, "CHALLENGE_BUSY"); return; }
        await JsonResponse.WriteAsync(request.Context, 200, new
        {
            challenge.Nonce,
            Proof = LicenseProof.FromChallenge(challenge)
        });
    }
}
