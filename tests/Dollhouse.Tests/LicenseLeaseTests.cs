using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Dollhouse.Core.Security;
using Dollhouse.Server.Accounts;
using Dollhouse.Server.Debugging;
using Dollhouse.Server.Echo;
using Dollhouse.Server.Endpoints;
using Dollhouse.Server.Licensing;
using Dollhouse.Server.Requests;

namespace Dollhouse.Tests;

internal static class LicenseLeaseTests
{
    private const string Hardware = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string ClientNonce = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    internal static async Task VerifyDeviceProofAsync(string publicKey, Func<byte[], byte[]> sign)
    {
        using var workspace = new TemporaryWorkspace();
        var clock = new Clock();
        var accounts = new LocalAccountStore(workspace.Paths);
        var login = accounts.Login("NativeDeviceFixture", "synthetic-device-password")!;
        var authority = LocalLicenseAuthority.Setup(workspace.Paths, clock: clock);
        var challenges = new LicenseChallengeStore(clock);
        using var services = new ServiceCollection().AddSingleton(new ServerPipeline(new Journal(), new NoRequestCapture(),
            [new LicenseChallengeEndpoint(accounts, challenges).Route,
             new LicenseLeaseEndpoint(accounts, challenges, authority, clock).Route])).BuildServiceProvider();
        var key = LicenseDeviceKey.Import(publicKey);
        var nonce = await Challenge(services, login, publicKey);
        var signature = sign(NativeProof(login.User.Id, key.Fingerprint, nonce));
        var body = LeaseBody(publicKey, nonce, signature);
        var response = await Send(services, LicenseLeaseEndpoint.Path, body, [login.Session]);
        Check.Equal(200, response.Response.StatusCode);
        VerifyNativeLease(response, authority, login.User.Id, key.Fingerprint, nonce, clock.GetUtcNow().ToUnixTimeSeconds());
        await ExpectError(services, body, [login.Session], "INVALID_CHALLENGE");
    }

    public static async Task VerifyAsync()
    {
        using var workspace = new TemporaryWorkspace();
        var clock = new Clock();
        var accounts = new LocalAccountStore(workspace.Paths);
        var login = accounts.Login("LeaseFixture", "synthetic-lease-password")!;
        var other = accounts.Login("OtherLeaseFixture", "synthetic-other-password")!;
        var authority = LocalLicenseAuthority.Setup(workspace.Paths, clock: clock);
        var stateBefore = File.ReadAllBytes(LocalLicenseAuthority.StatePath(workspace.Paths));
        var challenges = new LicenseChallengeStore(clock);
        using var services = new ServiceCollection().AddSingleton(new ServerPipeline(new Journal(), new NoRequestCapture(),
            [new LicenseChallengeEndpoint(accounts, challenges).Route,
             new LicenseLeaseEndpoint(accounts, challenges, authority, clock).Route])).BuildServiceProvider();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = LocalLicenseAuthority.Base64Url(key.ExportSubjectPublicKeyInfo());
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(key.ExportSubjectPublicKeyInfo()));
        var first = await Challenge(services, login, publicKey);
        var proof = NativeProof(login.User.Id, fingerprint, first);
        var signature = key.SignData(proof, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        // Canonicalization must agree with the independently assembled native proof bytes.
        Check.True(challenges.TryGet(first, out var stored), "Challenge is not available to lease handler.");
        Check.True(proof.SequenceEqual(LicenseProof.FromChallenge(stored!).Encode()), "Device proof bytes differ from native format.");
        var body = LeaseBody(publicKey, first, signature);

        Check.Equal(401, (await Send(services, LicenseLeaseEndpoint.Path, body, [])).Response.StatusCode);
        Check.Equal(401, (await Send(services, LicenseLeaseEndpoint.Path, body, [login.Session, other.Session])).Response.StatusCode);
        await ExpectError(services, body, [other.Session], "INVALID_CHALLENGE");
        var wrongHardware = JsonNode.Parse(body)!; wrongHardware["hw"] = new string('1', 64);
        await ExpectError(services, wrongHardware.ToJsonString(), [login.Session], "INVALID_CHALLENGE");
        var wrongNonce = JsonNode.Parse(body)!; wrongNonce["nonce"] = new string('0', 64);
        await ExpectError(services, wrongNonce.ToJsonString(), [login.Session], "INVALID_CHALLENGE");
        var wrongAction = JsonNode.Parse(body)!; wrongAction["action"] = "transfer";
        await ExpectError(services, wrongAction.ToJsonString(), [login.Session], "UNSUPPORTED_ACTION");
        using var wrongKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await ExpectError(services, LeaseBody(LocalLicenseAuthority.Base64Url(wrongKey.ExportSubjectPublicKeyInfo()), first, signature),
            [login.Session], "INVALID_CHALLENGE");
        var invalidSignature = (byte[])signature.Clone(); invalidSignature[^1] ^= 1;
        await ExpectError(services, LeaseBody(publicKey, first, invalidSignature), [login.Session], "BAD_DEVICE_PROOF");
        await ExpectError(services, LeaseBody(publicKey, first,
            key.SignData(proof, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)),
            [login.Session], "BAD_DEVICE_PROOF");
        var wrongClientProof = NativeProof(login.User.Id, fingerprint, first).Concat(new byte[] { 32 }).ToArray();
        await ExpectError(services, LeaseBody(publicKey, first,
            key.SignData(wrongClientProof, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)),
            [login.Session], "BAD_DEVICE_PROOF");
        Check.True(challenges.TryGet(first, out _), "Invalid proof consumed a valid challenge.");

        var accepted = await Send(services, LicenseLeaseEndpoint.Path, body, [login.Session, login.Session]);
        Check.Equal(200, accepted.Response.StatusCode);
        Check.Equal("no-store", accepted.Response.Headers.CacheControl.ToString());
        Check.Equal((long?)((MemoryStream)accepted.Response.Body).Length, accepted.Response.ContentLength);
        VerifyNativeLease(accepted, authority, login.User.Id, fingerprint, first, clock.GetUtcNow().ToUnixTimeSeconds());
        await ExpectError(services, body, [login.Session], "INVALID_CHALLENGE");

        var raceNonce = await Challenge(services, login, publicKey);
        var raceBody = LeaseBody(publicKey, raceNonce, key.SignData(NativeProof(login.User.Id, fingerprint, raceNonce),
            HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(index => Task.Run(() =>
            Send(services, LicenseLeaseEndpoint.Path, raceBody, [login.Session]))));
        Check.Equal(1, responses.Count(response => response.Response.StatusCode == 200));
        Check.Equal(7, responses.Count(response => response.Response.StatusCode == 400));

        var expired = await Challenge(services, login, publicKey);
        var expiredBody = LeaseBody(publicKey, expired, key.SignData(NativeProof(login.User.Id, fingerprint, expired),
            HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
        clock.Advance(LicenseChallengeStore.Lifetime);
        await ExpectError(services, expiredBody, [login.Session], "INVALID_CHALLENGE");
        foreach (var invalid in new[] { "{", "[]", "{}", "{\"public_key\":null}", body[..^1] + ",\"nonce\":\"duplicate\"}" })
            Check.Equal(400, (await Send(services, LicenseLeaseEndpoint.Path, invalid, [login.Session])).Response.StatusCode);
        Check.Equal(413, (await Send(services, LicenseLeaseEndpoint.Path, new string(' ', 8193), [login.Session])).Response.StatusCode);
        var fallback = await Send(services, LicenseLeaseEndpoint.Path, "", [login.Session], method: "GET");
        using (var json = Read(fallback)) Check.True(json.RootElement.TryGetProperty("body_length", out _), "GET unexpectedly granted a lease.");
        Check.True(stateBefore.SequenceEqual(File.ReadAllBytes(LocalLicenseAuthority.StatePath(workspace.Paths))), "Lease issuance rotated the authority.");
        var reopened = LocalLicenseAuthority.Setup(workspace.Paths, allowCreate: false, clock: clock);
        Check.Equal(authority.SigningPublicKey, reopened.SigningPublicKey);
        Check.Throws<InvalidOperationException>(() => reopened.SignLease(new { Fixture = true }, reopened.SigningNotAfter));
    }

    private static void VerifyNativeLease(HttpContext response, LocalLicenseAuthority authority, string account,
        string device, string nonce, long now)
    {
        using var envelope = Read(response);
        var outer = envelope.RootElement;
        Check.Equal(4, outer.EnumerateObject().Count());
        Check.Equal(3, outer.GetProperty("version").GetInt32());
        var keyId = outer.GetProperty("key_id").GetString();
        Check.Equal(LocalLicenseAuthority.SigningKeyId, keyId);
        var payload = LocalLicenseAuthority.DecodeBase64Url(outer.GetProperty("signed").GetString()!);
        var signature = LocalLicenseAuthority.DecodeBase64Url(outer.GetProperty("signature").GetString()!);
        // Resolve the lease key from the root-authenticated keyset, as the native verifier does.
        var keysetBytes = LocalLicenseAuthority.DecodeBase64Url(authority.Keyset.Signed);
        Check.True(VerifyEd25519(LocalLicenseAuthority.DecodeBase64Url(authority.RootPublicKey), keysetBytes,
            LocalLicenseAuthority.DecodeBase64Url(authority.Keyset.Signature)), "Keyset trust chain failed.");
        using var keyset = JsonDocument.Parse(keysetBytes);
        var leaf = keyset.RootElement.GetProperty("keys").EnumerateArray().Single(value => value.GetProperty("key_id").GetString() == keyId);
        var publicKey = LocalLicenseAuthority.DecodeBase64Url(leaf.GetProperty("public").GetString()!);
        Check.True(VerifyEd25519(publicKey, payload, signature), "Lease signature failed.");
        Check.True(!VerifyEd25519(LocalLicenseAuthority.DecodeBase64Url(authority.RootPublicKey), payload, signature), "Lease used the root instead of the advertised leaf.");
        var changed = (byte[])payload.Clone(); changed[^1] ^= 1;
        Check.True(!VerifyEd25519(publicKey, changed, signature), "Tampered lease was accepted.");
        using var json = JsonDocument.Parse(payload);
        var claims = json.RootElement;
        // Fields consumed at 0x1454d7100, including the shared verifier's payload key_id check.
        Check.Equal(keyId, claims.GetProperty("key_id").GetString());
        Check.Equal("lease", claims.GetProperty("type").GetString());
        Check.Equal("fallendoll", claims.GetProperty("product").GetString());
        Check.Equal(account, claims.GetProperty("account_id").GetString());
        Check.Equal(device, claims.GetProperty("device_id").GetString());
        Check.Equal(keyId, claims.GetProperty("aud").GetString());
        Check.Equal(nonce, claims.GetProperty("req_nonce").GetString());
        Check.Equal(ClientNonce, claims.GetProperty("client_nonce").GetString());
        Check.Equal(1, claims.GetProperty("ver").GetInt32());
        Check.Equal("lifetime", claims.GetProperty("entitlement").GetString());
        Check.Equal(JsonValueKind.Null, claims.GetProperty("sub_expires_at").ValueKind);
        var issued = claims.GetProperty("issued_at").GetInt64();
        var expires = claims.GetProperty("expires_at").GetInt64();
        Check.Equal(now, issued);
        Check.True(issued >= leaf.GetProperty("not_before").GetInt64() && issued < leaf.GetProperty("not_after").GetInt64(), "Lease issue time escaped signing-key validity.");
        Check.True(expires > issued && expires - issued <= 1_209_720, "Native lease duration limit exceeded.");
        Check.Equal(604800L, expires - issued);
    }

    private static byte[] NativeProof(string account, string device, string nonce) => Encoding.UTF8.GetBytes(
        "{\"account\":\"" + account + "\",\"action\":\"activate\",\"client_nonce\":\"" + ClientNonce
        + "\",\"device\":\"" + device + "\",\"nonce\":\"" + nonce + "\",\"product\":\"fallendoll\"}");

    private static string LeaseBody(string publicKey, string nonce, byte[] signature) =>
        JsonSerializer.Serialize(new { public_key = publicKey, action = "activate", nonce,
            signature = LocalLicenseAuthority.Base64Url(signature), hw = Hardware });

    private static async Task<string> Challenge(IServiceProvider services, LocalLogin login, string publicKey)
    {
        var response = await Send(services, LicenseChallengeEndpoint.Path,
            JsonSerializer.Serialize(new { public_key = publicKey, action = "activate", client_nonce = ClientNonce }), [login.Session]);
        Check.Equal(200, response.Response.StatusCode);
        using var json = Read(response);
        return json.RootElement.GetProperty("nonce").GetString()!;
    }

    private static bool VerifyEd25519(byte[] key, byte[] payload, byte[] signature)
    {
        var verifier = new Ed25519Signer(); verifier.Init(false, new Ed25519PublicKeyParameters(key));
        verifier.BlockUpdate(payload, 0, payload.Length);
        return verifier.VerifySignature(signature);
    }

    private static async Task ExpectError(IServiceProvider services, string body, string[] sessions, string error)
    {
        var response = await Send(services, LicenseLeaseEndpoint.Path, body, sessions);
        Check.Equal(400, response.Response.StatusCode);
        using var json = Read(response);
        Check.Equal(error, json.RootElement.GetProperty("error").GetString());
    }

    private static JsonDocument Read(HttpContext context) => JsonDocument.Parse(((MemoryStream)context.Response.Body).ToArray());
    private static async Task<HttpContext> Send(IServiceProvider services, string path, string body, string[] sessions, string method = "POST")
    {
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Method = method; context.Request.Path = path; context.Request.Protocol = "HTTP/1.1";
        context.Request.Headers["X-Rehost-Session"] = sessions;
        context.Request.Headers["X-Rehost-Device"] = Hardware;
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.Response.Body = new MemoryStream();
        await RequestDispatcher.HandleAsync(context);
        return context;
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        private long timestamp;
        public override DateTimeOffset GetUtcNow() => now;
        public override long GetTimestamp() => timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public void Advance(TimeSpan duration) { now += duration; timestamp += duration.Ticks; }
    }
    private sealed class Journal : IRequestJournal { public void Write(RequestMetadata request) { } }
}
