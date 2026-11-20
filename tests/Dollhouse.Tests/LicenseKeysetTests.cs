using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Dollhouse.Core;
using Dollhouse.Core.Patching;
using Dollhouse.Core.Security;
using Dollhouse.Core.Startup;
using Dollhouse.Server.Debugging;
using Dollhouse.Server.Echo;
using Dollhouse.Server.Endpoints;
using Dollhouse.Server.Requests;

namespace Dollhouse.Tests;

internal static class LicenseKeysetTests
{
    public static async Task VerifyAsync()
    {
        VerifyRfc8032Vector();
        using var workspace = new TemporaryWorkspace();
        Check.Throws<InvalidOperationException>(() => LocalLicenseAuthority.Setup(workspace.Paths, allowCreate: false));
        var authority = LocalLicenseAuthority.Setup(workspace.Paths);
        var stateFile = LocalLicenseAuthority.StatePath(workspace.Paths);
        var stored = File.ReadAllBytes(stateFile);
        var loaded = LocalLicenseAuthority.Setup(workspace.Paths, allowCreate: false);
        Check.Equal(authority.RootPublicKey, loaded.RootPublicKey);
        Check.Equal(authority.Keyset, loaded.Keyset);
        Check.True(stored.SequenceEqual(File.ReadAllBytes(stateFile)), "Repeat setup rewrote signing identity.");
        Check.True(authority.RootPublicKey != authority.SigningPublicKey, "Root and lease keys were reused.");
        VerifyContract(authority);

        using var services = new ServiceCollection().AddSingleton(new ServerPipeline(
            new SilentJournal(), new NoRequestCapture(), [new LicenseKeysetEndpoint(authority).Route])).BuildServiceProvider();
        // This is the observed duplicate header shape; no real session value is used.
        var first = await SendAsync(services, "POST", ["synthetic-session", "synthetic-session"]);
        Check.Equal(200, first.Response.StatusCode);
        Check.Equal("no-store", first.Response.Headers.CacheControl.ToString());
        using (var json = JsonDocument.Parse(((MemoryStream)first.Response.Body).ToArray()))
        {
            Check.Equal(4, json.RootElement.EnumerateObject().Count());
            Check.Equal(3, json.RootElement.GetProperty("version").GetInt32());
            Check.Equal(LocalLicenseAuthority.RootKeyId, json.RootElement.GetProperty("key_id").GetString());
            Check.Equal(authority.Keyset.Signed, json.RootElement.GetProperty("signed").GetString());
            Check.Equal(authority.Keyset.Signature, json.RootElement.GetProperty("signature").GetString());
            Check.Equal(first.Response.ContentLength, (long?)((MemoryStream)first.Response.Body).Length);
        }
        var concurrent = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => SendAsync(services, "POST", [])));
        Check.True(concurrent.All(context => ((MemoryStream)context.Response.Body).ToArray()
            .SequenceEqual(((MemoryStream)first.Response.Body).ToArray())), "Concurrent keysets changed identity or signature.");
        var wrongMethod = await SendAsync(services, "GET", []);
        using (var echo = JsonDocument.Parse(((MemoryStream)wrongMethod.Response.Body).ToArray()))
            Check.True(echo.RootElement.TryGetProperty("body_length", out _), "Unobserved GET was incorrectly registered.");
        using (var state = JsonDocument.Parse(stored))
            foreach (var name in new[] { "root_seed", "signing_seed" })
                Check.True(!Encoding.UTF8.GetString(((MemoryStream)first.Response.Body).ToArray())
                    .Contains(state.RootElement.GetProperty(name).GetString()!), "Private signing material leaked into keyset.");

        File.WriteAllText(stateFile, "{invalid-signing-fixture}");
        Check.Throws<InvalidDataException>(() => LocalLicenseAuthority.Setup(workspace.Paths));
        Check.Equal("{invalid-signing-fixture}", File.ReadAllText(stateFile));
        File.WriteAllText(stateFile, "{\"version\":1,\"root_seed\":null}");
        Check.Throws<InvalidDataException>(() => LocalLicenseAuthority.Setup(workspace.Paths));
    }

    private static void VerifyContract(LocalLicenseAuthority authority)
    {
        var envelope = authority.Keyset;
        var payload = LocalLicenseAuthority.DecodeBase64Url(envelope.Signed);
        var signature = LocalLicenseAuthority.DecodeBase64Url(envelope.Signature);
        var root = LocalLicenseAuthority.DecodeBase64Url(authority.RootPublicKey);
        Check.Equal(32, root.Length);
        Check.Equal(64, signature.Length);
        Check.True(Verify(root, payload, signature), "Root signature failed.");
        var alteredPayload = (byte[])payload.Clone(); alteredPayload[^1] ^= 1;
        var alteredSignature = (byte[])signature.Clone(); alteredSignature[0] ^= 1;
        Check.True(!Verify(root, alteredPayload, signature), "Modified signed payload was accepted.");
        Check.True(!Verify(root, payload, alteredSignature), "Modified signature was accepted.");
        Check.True(!Verify(LocalLicenseAuthority.DecodeBase64Url(GameBuild.OldLicenseRoot), payload, signature),
            "Original operator root trusted local keyset.");
        using var json = JsonDocument.Parse(payload);
        var record = json.RootElement;
        Check.Equal("keyset", record.GetProperty("type").GetString());
        Check.Equal("fallendoll", record.GetProperty("product").GetString());
        Check.Equal(envelope.KeyId, record.GetProperty("key_id").GetString());
        Check.True(record.GetProperty("seq").GetInt64() > 0, "Invalid signed sequence.");
        Check.Equal(JsonValueKind.Number, record.GetProperty("issued_at").ValueKind);
        var keys = record.GetProperty("keys");
        Check.Equal(1, keys.GetArrayLength());
        Check.Equal(LocalLicenseAuthority.SigningKeyId, keys[0].GetProperty("key_id").GetString());
        Check.Equal(authority.SigningPublicKey, keys[0].GetProperty("public").GetString());
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Check.True(keys[0].GetProperty("not_before").GetInt64() <= now
            && keys[0].GetProperty("not_after").GetInt64() > now, "Signing key is not currently valid.");
        Check.Equal(0, record.GetProperty("revoked").GetArrayLength());
    }

    private static void VerifyRfc8032Vector()
    {
        // RFC 8032 section 7.1, test 1: published test material, never a real signing identity.
        var key = new Ed25519PrivateKeyParameters(Convert.FromHexString("9d61b19deffd5a60ba844af492ec2cc44449c5697b326919703bac031cae7f60"));
        Check.Equal("d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a",
            Convert.ToHexStringLower(key.GeneratePublicKey().GetEncoded()));
        var signer = new Ed25519Signer(); signer.Init(true, key);
        var signature = signer.GenerateSignature();
        Check.Equal("e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e065224901555fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b",
            Convert.ToHexStringLower(signature));
        Check.True(Verify(key.GeneratePublicKey().GetEncoded(), [], signature), "RFC 8032 verification failed.");
    }

    private static bool Verify(byte[] key, byte[] payload, byte[] signature)
    {
        var verifier = new Ed25519Signer(); verifier.Init(false, new Ed25519PublicKeyParameters(key));
        verifier.BlockUpdate(payload, 0, payload.Length);
        return verifier.VerifySignature(signature);
    }

    private sealed class SilentJournal : IRequestJournal { public void Write(Dollhouse.Server.Echo.RequestMetadata metadata) { } }

    private static async Task<HttpContext> SendAsync(IServiceProvider services, string method, string[] sessions)
    {
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Method = method;
        context.Request.Path = LicenseKeysetEndpoint.Path;
        context.Request.Protocol = "HTTP/1.1";
        context.Request.Headers["X-Rehost-Session"] = sessions;
        context.Request.Body = new MemoryStream("{}"u8.ToArray());
        context.Response.Body = new MemoryStream();
        await RequestDispatcher.HandleAsync(context);
        return context;
    }
}
