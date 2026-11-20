using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Dollhouse.Core.Security;
using Dollhouse.Server.Accounts;
using Dollhouse.Server.Debugging;
using Dollhouse.Server.Echo;
using Dollhouse.Server.Endpoints;
using Dollhouse.Server.Licensing;
using Dollhouse.Server.Requests;

namespace Dollhouse.Tests;

internal static class LicenseChallengeTests
{
    private const string Device = "1111111111111111111111111111111111111111111111111111111111111111";
    private const string ClientNonce = "222222222222222222222222222222222222222222222222";

    public static async Task VerifyAsync()
    {
        using var workspace = new TemporaryWorkspace();
        var accounts = new LocalAccountStore(workspace.Paths);
        var login = accounts.Login("ChallengeFixture", "synthetic-challenge-password")!;
        var clock = new Clock();
        var challenges = new LicenseChallengeStore(clock);
        var journal = new Journal();
        using var services = new ServiceCollection().AddSingleton(new ServerPipeline(journal, new NoRequestCapture(),
            [new LicenseChallengeEndpoint(accounts, challenges).Route, new UserMeEndpoint(accounts).Route])).BuildServiceProvider();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = LocalLicenseAuthority.Base64Url(key.ExportSubjectPublicKeyInfo());
        var body = JsonSerializer.Serialize(new { public_key = publicKey, action = "activate", client_nonce = ClientNonce });
        var context = await Send(services, body, [login.Session, login.Session]);
        Check.Equal(200, context.Response.StatusCode);
        Check.Equal("no-store", context.Response.Headers.CacheControl.ToString());
        Check.Equal((long?)((MemoryStream)context.Response.Body).Length, context.Response.ContentLength);
        var nonce = ReadNonce(context);
        Check.Equal(64, nonce.Length);
        Check.True(nonce.All(char.IsAsciiHexDigitLower), "Challenge is not native-safe text.");
        Check.True(challenges.TryGet(nonce, out var issued), "Issued challenge was not retained.");
        var licenseDevice = Convert.ToHexStringLower(SHA256.HashData(key.ExportSubjectPublicKeyInfo()));
        var binding = new LicenseChallengeBinding(login.User.Id, login.Session, Device, licenseDevice, publicKey, "activate", ClientNonce);
        Check.Equal(binding, issued!.Binding);
        VerifyNativeResponse(context, binding);
        Check.Equal(clock.GetUtcNow().Add(LicenseChallengeStore.Lifetime).ToUnixTimeSeconds(), issued.ExpiresAt);
        foreach (var wrong in new[] { binding with { AccountId = "different-account" }, binding with { Session = "different-session" },
            binding with { HeaderDevice = "different-installation" }, binding with { Device = "different-device" }, binding with { PublicKey = "different-key" },
            binding with { Action = "different-action" }, binding with { ClientNonce = "different-client-nonce" },
            binding with { Product = "different-product" } })
            Check.True(!challenges.TryConsume(nonce, wrong, out _), "Challenge accepted mismatched context.");
        Check.True(challenges.TryConsume(nonce, binding, out _), "Correct challenge binding failed.");
        Check.True(!challenges.TryConsume(nonce, binding, out _), "Consumed nonce was reusable.");
        Check.True(!challenges.TryGet(nonce, out _), "Consumed nonce remained available.");
        var simultaneous = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() => Send(services, body, [login.Session]))));
        var nonces = simultaneous.Select(ReadNonce).ToArray();
        Check.Equal(12, nonces.Distinct().Count());
        var consumers = await Task.WhenAll(Enumerable.Range(0, 8).Select(index => Task.Run(() => challenges.TryConsume(nonces[0], binding, out _))));
        Check.Equal(1, consumers.Count(success => success));
        // Wall-clock changes must not extend the monotonic lifetime.
        clock.WallTime = clock.WallTime.AddDays(-1);
        clock.AdvanceMonotonic(LicenseChallengeStore.Lifetime);
        Check.True(!challenges.TryGet(nonces[1], out _), "Expired nonce survived wall-clock rollback.");
        Check.True(!challenges.TryConsume(nonces[2], binding, out _), "Expired nonce was consumable.");
        Check.True(!new LicenseChallengeStore(clock).TryGet(nonces[3], out _), "Restart retained a process-local challenge.");

        Check.Equal(401, (await Send(services, body, [])).Response.StatusCode);
        Check.Equal(401, (await Send(services, body, [new string('0', 64)])).Response.StatusCode);
        Check.Equal(401, (await Send(services, body, [login.Session, new string('0', 64)])).Response.StatusCode);
        Check.Equal(401, (await Send(services, body, [login.Session + ", " + login.Session])).Response.StatusCode);
        Check.Equal(400, (await Send(services, body, [login.Session], device: null)).Response.StatusCode);
        Check.Equal(400, (await Send(services, body, [login.Session], device: "invalid")).Response.StatusCode);
        using var wrongCurve = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        foreach (var invalid in new[] { "{", "[]", "{}", "{\"public_key\":null}",
            body.Replace("\"activate\"", "1"), body.Replace("\"activate\"", "\"\""), body.Replace(ClientNonce, "invalid"),
            body.Replace(publicKey, "invalid"), body.Replace(publicKey, LocalLicenseAuthority.Base64Url(wrongCurve.ExportSubjectPublicKeyInfo())),
            body.Replace(publicKey, LocalLicenseAuthority.Base64Url([.. key.ExportSubjectPublicKeyInfo(), 0])),
            body[..^1] + ",\"action\":\"activate\"}" })
            Check.Equal(400, (await Send(services, invalid, [login.Session])).Response.StatusCode);
        Check.Equal(413, (await Send(services, new string(' ', 4097), [login.Session])).Response.StatusCode);
        // Duplicate identical sessions now use the shared resolver on cached-login lookups too.
        Check.Equal(200, (await Send(services, "", [login.Session, login.Session], path: UserMeEndpoint.Path, method: "GET")).Response.StatusCode);
        Check.Equal(401, (await Send(services, "", [login.Session, new string('0', 64)], path: UserMeEndpoint.Path, method: "GET")).Response.StatusCode);
        var wrongMethod = await Send(services, "", [login.Session], method: "GET");
        using (var json = JsonDocument.Parse(((MemoryStream)wrongMethod.Response.Body).ToArray()))
            Check.True(json.RootElement.TryGetProperty("body_length", out _), "Unsupported method did not echo.");
        Check.True(journal.Handled > 0, "Challenge was not classified as an API.");
        await VerifyNativeDeviceFingerprint(services, login.Session);
        VerifyCapacity(binding);
    }

    private static void VerifyCapacity(LicenseChallengeBinding binding)
    {
        var clock = new Clock();
        var store = new LicenseChallengeStore(clock);
        for (var i = 0; i < LicenseChallengeStore.Capacity; i++)
            Check.True(store.Issue(binding) is not null, "Store filled before its declared bound.");
        Check.True(store.Issue(binding) is null, "Challenge store grew past its bound.");
        clock.AdvanceMonotonic(LicenseChallengeStore.Lifetime);
        Check.True(store.Issue(binding) is not null, "Expired entries prevented fresh issuance.");
    }

    private static async Task VerifyNativeDeviceFingerprint(IServiceProvider services, string session)
    {
        // Public NIST P-256 base point in the exact SPKI prefix + X/Y format traced at 0x1454dd323.
        // Fixed public test material lets this catch using the transport ID, EC point, or encoded text as hash input.
        var der = Convert.FromHexString("3059301306072a8648ce3d020106082a8648ce3d03010703420004"
            + "6b17d1f2e12c4247f8bce6e563a440f277037d812deb33a0f4a13945d898c296"
            + "4fe342e2fe1a7f9b8ee7eb4a7c0f9e162bce33576b315ececbb6406837bf51f5");
        var body = JsonSerializer.Serialize(new { public_key = LocalLicenseAuthority.Base64Url(der), action = "activate", client_nonce = ClientNonce });
        var response = await Send(services, body, [session]);
        Check.Equal(200, response.Response.StatusCode);
        using var json = JsonDocument.Parse(((MemoryStream)response.Response.Body).ToArray());
        Check.Equal("5cd252fb0ce8932436faf8ccd1040981b89ee4ad6b9fe9e2a2b7e71aacb27cd3",
            json.RootElement.GetProperty("proof").GetProperty("device").GetString());
    }

    private static string ReadNonce(HttpContext context)
    {
        using var json = JsonDocument.Parse(((MemoryStream)context.Response.Body).ToArray());
        Check.Equal(2, json.RootElement.EnumerateObject().Count());
        return json.RootElement.GetProperty("nonce").GetString()!;
    }

    private static void VerifyNativeResponse(HttpContext context, LicenseChallengeBinding binding)
    {
        var bytes = ((MemoryStream)context.Response.Body).ToArray();
        using var json = JsonDocument.Parse(bytes);
        Check.True(NativeAccepts(json.RootElement, binding), "Native challenge consumer would reject the response.");
        var nonce = json.RootElement.GetProperty("nonce").GetString();
        using var oldResponse = JsonDocument.Parse(JsonSerializer.Serialize(new { nonce }));
        Check.True(!NativeAccepts(oldResponse.RootElement, binding), "Nonce-only regression was not detected.");
        foreach (var field in new[] { "account", "action", "client_nonce", "device", "nonce", "product" })
        {
            var changed = JsonNode.Parse(bytes)!;
            changed["proof"]![field] = "different-fixture-context";
            using var altered = JsonDocument.Parse(changed.ToJsonString());
            Check.True(!NativeAccepts(altered.RootElement, binding), "Native proof comparison accepted a mismatched field.");
        }
        var extra = JsonNode.Parse(bytes)!;
        extra["proof"]!["unexpected"] = "fixture";
        using var extended = JsonDocument.Parse(extra.ToJsonString());
        Check.True(!NativeAccepts(extended.RootElement, binding), "Native proof key-set restriction was not enforced.");
    }

    // Static model of 0x1454d9b58..0x1454da062 and exact-key helper 0x1454dc5d0.
    // The client requires this proof BEFORE signing and sending the lease request.
    private static bool NativeAccepts(JsonElement response, LicenseChallengeBinding binding)
    {
        if (!response.TryGetProperty("nonce", out var nonce) || nonce.ValueKind != JsonValueKind.String
            || !response.TryGetProperty("proof", out var proof) || proof.ValueKind != JsonValueKind.Object)
            return false;
        var expected = new Dictionary<string, string>
        {
            ["account"] = binding.AccountId, ["action"] = binding.Action, ["client_nonce"] = binding.ClientNonce,
            ["device"] = binding.Device, ["nonce"] = nonce.GetString()!, ["product"] = "fallendoll"
        };
        if (proof.EnumerateObject().Count() != expected.Count) return false;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in proof.EnumerateObject())
            if (!visited.Add(property.Name) || !expected.TryGetValue(property.Name, out var value)
                || property.Value.ValueKind != JsonValueKind.String || property.Value.GetString() != value)
                return false;
        return true;
    }

    private static async Task<HttpContext> Send(IServiceProvider services, string body, string[] sessions,
        string? device = Device, string path = LicenseChallengeEndpoint.Path, string method = "POST")
    {
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Method = method;
        context.Request.Path = path;
        context.Request.Protocol = "HTTP/1.1";
        context.Request.Headers["X-Rehost-Session"] = sessions;
        if (device is not null) context.Request.Headers["X-Rehost-Device"] = device;
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.Response.Body = new MemoryStream();
        await RequestDispatcher.HandleAsync(context);
        return context;
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset WallTime { get; set; } = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        private long timestamp;
        public override DateTimeOffset GetUtcNow() => WallTime;
        public override long GetTimestamp() => timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public void AdvanceMonotonic(TimeSpan duration) => timestamp += duration.Ticks;
    }

    private sealed class Journal : IRequestJournal
    {
        private int handled;
        public int Handled => handled;
        public void Write(RequestMetadata metadata) { if (metadata.Handling == RequestHandling.Api) Interlocked.Increment(ref handled); }
    }
}
