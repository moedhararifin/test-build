using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Dollhouse.Core;
using Dollhouse.Core.Diagnostics;
using Dollhouse.Core.Patching;
using Dollhouse.Core.Security;
using Dollhouse.Core.Startup;
using Dollhouse.Server;
using Dollhouse.Server.Echo;

namespace Dollhouse.Tests;

internal static class IntegrationSuite
{
    public static async Task<int> RunAsync()
    {
        using var workspace = new TemporaryWorkspace();
        var material = LocalCertificate.Setup(workspace.Paths);
        using var certificate = X509CertificateLoader.LoadCertificateFromFile(workspace.Paths.Certificate);
        var journal = new CaptureJournal();
        await using var server = ServerApplication.Build(workspace.Paths, 0, journal);
        await server.StartAsync();
        var address = server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        var port = new Uri(address).Port;
        using var client = new HttpClient(PinnedHttpsProbe.CreateHandler(certificate))
        { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(10) };
        var failures = 0;
        var count = 0;
        async Task Test(string name, Func<Task> action)
        {
            count++;
            try { await action(); Console.WriteLine($"PASS {name}"); }
            catch (Exception error) { failures++; Console.Error.WriteLine($"FAIL {name}: {error}"); }
        }

        await Test("server modules dispatch and observe without networking", ServerModuleTests.VerifyAsync);
        await Test("pakkey endpoint response and private configuration", PakKeyTests.VerifyAsync);
        await Test("shared JSON response compatibility", JsonResponseTests.VerifyAsync);
        await Test("notice and user-hint response contracts", UpdateEndpointTests.VerifyAsync);
        await Test("user PoW optional-work contract", UserPowTests.VerifyAsync);
        await Test("local login and cached session identity", LoginEndpointTests.VerifyAsync);
        await Test("signed local license keyset and reversible trust patch", LicenseKeysetTests.VerifyAsync);
        await Test("bound local license challenges and session headers", LicenseChallengeTests.VerifyAsync);
        await Test("device proof and signed local lease", LicenseLeaseTests.VerifyAsync);
        await Test("empty manifest refusal", ManifestTests.VerifyAsync);
        await Test("local emulator welcome mail", MailEndpointTests.VerifyAsync);
        await Test("local client report acknowledgement", ClientReportTests.VerifyAsync);
        await Test("local account email association", UserEmailTests.VerifyAsync);
        await Test("local synchronized profile collection", AccountsTests.VerifyAsync);
        await Test("local profile ownership and save lifecycle", ProfileSessionTests.VerifyAsync);
        await Test("concurrent capture notifications stay grouped", DebugCaptureTests.VerifyGroupedNotificationsAsync);

        await Test("certificate generation preserves existing certificate and key", () =>
        {
            var certBytes = File.ReadAllBytes(workspace.Paths.Certificate);
            var keyBytes = File.ReadAllBytes(workspace.Paths.PrivateKey);
            Check.Equal(material.CertificateSha256, LocalCertificate.Setup(workspace.Paths).CertificateSha256);
            Check.True(certBytes.AsSpan().SequenceEqual(File.ReadAllBytes(workspace.Paths.Certificate)), "Certificate changed.");
            Check.True(keyBytes.AsSpan().SequenceEqual(File.ReadAllBytes(workspace.Paths.PrivateKey)), "Key changed.");
            return Task.CompletedTask;
        });
        await Test("incomplete TLS material is rejected", () =>
        {
            using var incomplete = new TemporaryWorkspace();
            Directory.CreateDirectory(incomplete.Paths.Tls);
            File.Copy(workspace.Paths.Certificate, incomplete.Paths.Certificate);
            Check.Throws<InvalidOperationException>(() => LocalCertificate.Setup(incomplete.Paths));
            return Task.CompletedTask;
        });
        await Test("listener binds only IPv4 loopback", () =>
        {
            Check.True(address.StartsWith("https://127.0.0.1:", StringComparison.Ordinal), "Unexpected bind address.");
            return Task.CompletedTask;
        });
        await Test("HTTP methods, raw target, header values and binary bodies", async () =>
        {
            var body = new byte[] { 0, 255, 128, 13, 10, 97 };
            foreach (var method in new[] { "GET", "POST", "PUT", "PATCH", "DELETE", "OPTIONS", "CUSTOM" })
            {
                using var request = new HttpRequestMessage(new HttpMethod(method), "/api/v1/test?q=a%20b")
                { Content = new ByteArrayContent(body) };
                request.Headers.Add("X-Echo-Test", ["one", "two"]);
                using var response = await client.SendAsync(request);
                Check.Equal(HttpStatusCode.OK, response.StatusCode);
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Check.Equal(method, json.RootElement.GetProperty("method").GetString());
                Check.Equal("/api/v1/test?q=a%20b", json.RootElement.GetProperty("target").GetString());
                Check.True(body.AsSpan().SequenceEqual(Convert.FromBase64String(json.RootElement.GetProperty("body_base64").GetString()!)), "Binary data changed.");
                var values = json.RootElement.GetProperty("headers").EnumerateArray()
                    .Where(pair => pair[0].GetString() == "X-Echo-Test").Select(pair => pair[1].GetString());
                Check.True(string.Join(",", values).Contains("one") && string.Join(",", values).Contains("two"), "Header values missing.");
            }
        });
        await Test("HEAD has response length and no body", async () =>
        {
            using var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/head"));
            Check.Equal(HttpStatusCode.OK, response.StatusCode);
            Check.True(response.Content.Headers.ContentLength > 0, "Missing length.");
            Check.Equal(0, (await response.Content.ReadAsByteArrayAsync()).Length);
        });
        await Test("chunked upload is reconstructed exactly", async () =>
        {
            var raw = await RawRequestAsync(port, certificate, "POST /chunks HTTP/1.1\r\nHost: localhost\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n3\r\none\r\n3\r\ntwo\r\n0\r\n\r\n");
            Check.True(raw.Contains("200 OK") && raw.Contains("b25ldHdv"), "Chunked body not echoed.");
        });
        await Test("oversized and ambiguous HTTP framing is rejected", async () =>
        {
            var oversized = await RawRequestAsync(port, certificate, $"POST /big HTTP/1.1\r\nHost: localhost\r\nContent-Length: {EchoEndpoint.MaximumBodySize + 1}\r\nConnection: close\r\n\r\n");
            Check.True(oversized.Contains("413"), "Oversized request not rejected.");
            var ambiguous = await RawRequestAsync(port, certificate, "POST /bad HTTP/1.1\r\nHost: localhost\r\nContent-Length: 0\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n0\r\n\r\n");
            Check.True(ambiguous.Contains("400"), "Ambiguous framing not rejected.");
        });
        await Test("default TLS client rejects the self-signed certificate", async () =>
        {
            using var untrusted = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
            await Check.ThrowsAsync<HttpRequestException>(async () => { using var _ = await untrusted.GetAsync(address); });
        });
        await Test("incorrect pin rejects the connection before HTTP reaches the handler", async () =>
        {
            var before = journal.Items.Count;
            using var wrong = new HttpClient(PinnedHttpsProbe.CreateHandler(certificate, new string('0', 64))) { Timeout = TimeSpan.FromSeconds(5) };
            await Check.ThrowsAsync<HttpRequestException>(async () => { using var _ = await wrong.GetAsync(address + "/wrong-pin"); });
            Check.Equal(before, journal.Items.Count);
        });
        await Test("logs redact credentials and exclude bodies", () =>
        {
            var safe = RequestRedactor.Redact(new(DateTimeOffset.UtcNow, "POST", "/login?token=SECRET&ok=yes", [["Authorization", "SECRET"], ["Cookie", "SECRET"], ["X-Echo", "visible"]], 10));
            var json = JsonOutput.Serialize(safe);
            Check.True(!json.Contains("SECRET") && !json.Contains("body_text") && json.Contains("visible"), "Secret/body leakage.");
            return Task.CompletedTask;
        });
        await Test("WebSocket text, binary, continuation and close", async () =>
        {
            using var socket = new ClientWebSocket();
            socket.Options.RemoteCertificateValidationCallback = (_, cert, _, errors) => PinnedHttpsProbe.ValidateCertificate(cert, certificate, material.CertificateSha256, errors);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await socket.ConnectAsync(new Uri($"wss://127.0.0.1:{port}/api/v1/game?profile=test"), timeout.Token);
            await socket.SendAsync("hello"u8.ToArray(), WebSocketMessageType.Text, true, timeout.Token);
            Check.Equal("hello", Encoding.UTF8.GetString(await ReadMessageAsync(socket, timeout.Token)));
            var binary = Enumerable.Range(0, 256).Select(x => (byte)x).ToArray();
            await socket.SendAsync(binary.AsMemory(0, 100), WebSocketMessageType.Binary, false, timeout.Token);
            await socket.SendAsync(binary.AsMemory(100), WebSocketMessageType.Binary, true, timeout.Token);
            var echoed = await ReadMessageAsync(socket, timeout.Token);
            Check.True(binary.AsSpan().SequenceEqual(echoed), "Binary message changed.");
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", timeout.Token);
            Check.Equal(WebSocketState.Closed, socket.State);
        });
        await Test("WebSocket ping receives matching pong", async () =>
        {
            using var stream = await ConnectTlsAsync(port, certificate);
            var key = Convert.ToBase64String(Guid.NewGuid().ToByteArray());
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"GET /ping HTTP/1.1\r\nHost: localhost\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Version: 13\r\nSec-WebSocket-Key: {key}\r\n\r\n"));
            var headers = new List<byte>();
            var one = new byte[1];
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!Encoding.ASCII.GetString(headers.ToArray()).EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                await stream.ReadExactlyAsync(one, timeout.Token);
                headers.Add(one[0]);
                Check.True(headers.Count < 8192, "Invalid upgrade response.");
            }
            Check.True(Encoding.ASCII.GetString(headers.ToArray()).Contains("101"), "Upgrade failed.");
            await stream.WriteAsync(new byte[] { 0x89, 0x81, 1, 2, 3, 4, (byte)('x' ^ 1) }, timeout.Token);
            var pong = new byte[3];
            await stream.ReadExactlyAsync(pong, timeout.Token);
            Check.True(pong.AsSpan().SequenceEqual(new byte[] { 0x8a, 1, (byte)'x' }), "Pong mismatch.");
        });
        await Test("pinned HTTPS diagnostic verifies echo", async () => { _ = await PinnedHttpsProbe.RunAsync(workspace.Paths, port); });
        if (OperatingSystem.IsWindows())
            await Test("native Windows WinHTTP diagnostic verifies TLS and DER pin", () => { _ = WinHttpProbe.Run(workspace.Paths, port); return Task.CompletedTask; });
        await Test("normal mode has not created raw capture files", () =>
        {
            Check.True(!Directory.Exists(Path.Combine(workspace.Paths.Local, "debug")), "Raw captures were enabled by default.");
            return Task.CompletedTask;
        });
        await Test("copyable development curl commands target the original origin with --insecure", DebugCaptureTests.VerifyExporterAsync);
        await Test("debug captures exact HTTP/WSS payloads and pasted curl commands work against a local HTTPS mock",
            () => DebugCaptureTests.VerifyCaptureAndReplayAsync(workspace.Paths, address));
        await Test("game discovery uses EXE folder instead of requiring a source checkout", () =>
        {
            using var game = new TemporaryWorkspace();
            var shipping = Path.Combine(game.Paths.Root, "Paralogue", "Binaries", "Win64", "Paralogue-Win64-Shipping.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(shipping)!);
            File.WriteAllText(shipping, "discovery fixture; never executed");
            var found = GameInstallation.Locate(null, game.Paths.Root, workspace.Paths.Root);
            Check.Equal(shipping, found.Executable);
            Check.Equal(game.Paths.Root, found.Root);
            Check.Equal(found, GameInstallation.Locate(shipping, workspace.Paths.Root, workspace.Paths.Root));
            Check.Equal(found, GameInstallation.Locate(null, Path.GetDirectoryName(shipping)!, workspace.Paths.Root));
            return Task.CompletedTask;
        });
        await Test("missing or unsupported target does not create backup/certificate files", () =>
        {
            using var game = new TemporaryWorkspace();
            Check.Throws<FileNotFoundException>(() => GameInstallation.Locate(null, game.Paths.Root, game.Paths.Root));
            var target = Path.Combine(game.Paths.Root, "game.exe");
            File.WriteAllText(target, "not a supported binary");
            Check.Throws<InvalidDataException>(() => GamePreparation.Prepare(new(target, game.Paths.Root)));
            Check.True(!File.Exists(GameBinary.BackupPath(target)) && !Directory.Exists(Path.Combine(game.Paths.Root, ".dollhouse")), "Unsupported game was modified.");
            return Task.CompletedTask;
        });
        var originalPath = GameBinaryFixture.Original;
        if (originalPath is not null)
        {
            await Test("automatic preparation needs no backup, preserves original EXE and reuses identities", () =>
            {
                using var game = new TemporaryWorkspace();
                var target = Path.Combine(game.Paths.Root, "game.exe");
                File.Copy(originalPath, target);
                var installation = new GameInstallation(target, game.Paths.Root);
                var first = GamePreparation.Prepare(installation);
                var backup = GameBinary.BackupPath(target);
                Check.True(!File.Exists(backup), "Fresh startup created a backup.");
                Check.Equal(first.CertificateSha256, GamePreparation.Prepare(installation).CertificateSha256);
                // A new valid local certificate may replace our prior pin, never unrelated bytes.
                File.Copy(workspace.Paths.Certificate, first.Paths.Certificate, overwrite: true);
                File.Copy(workspace.Paths.PrivateKey, first.Paths.PrivateKey, overwrite: true);
                var repinned = GamePreparation.Prepare(installation);
                Check.Equal(material.CertificateSha256, repinned.CertificateSha256);
                using (GameBinary.OpenVerifiedOriginal(target)) { }
                Check.True(!File.Exists(backup), "Repeat startup created a backup.");
                File.WriteAllText(backup, "do not overwrite this existing backup");
                Check.Throws<InvalidDataException>(() => GamePreparation.Prepare(installation));
                Check.Equal("do not overwrite this existing backup", File.ReadAllText(backup));
                return Task.CompletedTask;
            });
            await Test("patched install refuses a manual .bak when its canonical backup is missing", () =>
            {
                using var game = new TemporaryWorkspace();
                var target = Path.Combine(game.Paths.Root, "game.exe");
                File.Copy(originalPath, target);
                var backup = GameBinary.BackupPath(target);
                File.WriteAllText(target, "modified fixture");
                File.Copy(originalPath, target + ".bak" );
                Check.Throws<InvalidDataException>(() => GamePreparation.Prepare(new(target, game.Paths.Root)));
                Check.True(!File.Exists(backup), "Manual file was migrated into an automatic backup.");
                Check.Equal(GameBuild.OriginalSha256, GameBinary.Hash(File.ReadAllBytes(target + ".bak")));
                return Task.CompletedTask;
            });
            var app = Path.Combine(WorkspacePaths.Discover().Root, "dist", "Dollhouse.exe");
            if (OperatingSystem.IsWindows() && File.Exists(app))
                await Test("published EXE: automatic discovery, backup, certificate, patch, in-console logs, duplicate guard and explicit-target restart",
                    () => PublishedAppTests.RunAsync(app, originalPath));
        }
        else Console.WriteLine("SKIP real-sample test: supply DOLLHOUSE_TEST_GAME or .local/fixtures/Paralogue-Win64-Shipping.exe.");
        await server.StopAsync();
        Console.WriteLine($"{count - failures}/{count} passed; no game execution or external service requests.");
        return failures == 0 ? 0 : 1;
    }

    private static async Task<byte[]> ReadMessageAsync(ClientWebSocket socket, CancellationToken token)
    {
        using var result = new MemoryStream();
        var buffer = new byte[4096];
        ValueWebSocketReceiveResult frame;
        do { frame = await socket.ReceiveAsync(buffer.AsMemory(), token); result.Write(buffer, 0, frame.Count); }
        while (!frame.EndOfMessage);
        return result.ToArray();
    }

    private static async Task<SslStream> ConnectTlsAsync(int port, X509Certificate2 trusted)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(IPAddress.Loopback, port);
        var stream = new SslStream(new NetworkStream(socket, ownsSocket: true), false,
            (_, certificate, _, errors) => PinnedHttpsProbe.ValidateCertificate(certificate, trusted, LocalCertificate.Fingerprint(trusted), errors));
        await stream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "127.0.0.1" });
        return stream;
    }

    private static async Task<string> RawRequestAsync(int port, X509Certificate2 certificate, string request)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var stream = await ConnectTlsAsync(port, certificate);
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request), timeout.Token);
        using var body = new MemoryStream();
        await stream.CopyToAsync(body, timeout.Token);
        return Encoding.UTF8.GetString(body.ToArray());
    }

    private sealed class CaptureJournal : IRequestJournal
    {
        public ConcurrentQueue<RequestMetadata> Items { get; } = new();
        public void Write(RequestMetadata metadata) => Items.Enqueue(RequestRedactor.Redact(metadata));
    }
}
