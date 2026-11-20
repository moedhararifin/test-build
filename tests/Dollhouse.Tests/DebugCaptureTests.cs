using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Dollhouse.Core;
using Dollhouse.Core.Diagnostics;
using Dollhouse.Core.Security;
using Dollhouse.Server;
using Dollhouse.Server.Debugging;
using Dollhouse.Server.Echo;
using Dollhouse.Server.Endpoints;
using Dollhouse.Server.Requests;
using Dollhouse.App;

namespace Dollhouse.Tests;

internal static class DebugCaptureTests
{
    public static async Task VerifyGroupedNotificationsAsync()
    {
        using var workspace = new TemporaryWorkspace();
        var capture = new DebugRequestCapture(workspace.Paths);
        var pipeline = new ServerPipeline(new TerminalRequestJournal(), capture, [new UserHintEndpoint().Route]);
        using var output = new StringWriter();
        var previous = Console.Out;
        try
        {
            Console.SetOut(output);
            await Task.WhenAll(Enumerable.Range(0, 12).Select(index =>
            {
                var target = index % 2 == 0 ? $"/api/v1/manifest?n={index}" : $"/api/v1/user/hint?n={index}";
                var context = new DefaultHttpContext();
                context.Request.Method = "GET";
                context.Request.Path = target.Split('?')[0];
                context.Response.Body = new MemoryStream();
                var message = new EchoRequest(DateTimeOffset.UtcNow, "GET", target, "HTTP/1.1", [], 0, "", "");
                return pipeline.HandleAsync(new ServerRequest(context, message, ReadOnlyMemory<byte>.Empty), default);
            }));
        }
        finally { Console.SetOut(previous); }

        // Inspect the actual app renderer, not just a capture callback: each complete
        // block must begin with its main request row and end with that request's curl.
        var blocks = output.ToString().Replace("\r\n", "\n").Trim().Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        Check.Equal(12, blocks.Length);
        for (var index = 0; index < blocks.Length; index++)
        {
            var lines = blocks[index].Split('\n');
            Check.Equal(4, lines.Length);
            var id = (index + 1).ToString("D6");
            Check.Equal($"DEBUG #{id}", lines[1].Trim());
            var directory = Path.Combine(capture.SessionDirectory, id);
            using var savedRequest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "request.json")));
            var target = savedRequest.RootElement.GetProperty("target").GetString()!;
            Check.True(lines[0].Contains("GET") && lines[0].Contains(target), "Main row is not adjacent to its own capture details.");
            Check.True(lines[0].Contains(target.StartsWith(UserHintEndpoint.Path, StringComparison.Ordinal) ? "[API]" : "[ECHO]"),
                "Request row did not identify the selected handler versus fallback.");
            using var savedCapture = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "capture.json")));
            Check.Equal(savedCapture.RootElement.GetProperty("curl_command").GetString(), lines[3]);
            Check.True(lines[3].StartsWith("curl.exe ", StringComparison.Ordinal), "Copyable command lost its own line.");
        }
    }

    public static Task VerifyExporterAsync()
    {
        var request = new EchoRequest(DateTimeOffset.UtcNow, "POST", "/api/v1/pakkey?q=a%20b&n={x}", "HTTP/1.1",
            [["Host", "127.0.0.1:7900"], ["Content-Length", "2"], ["Transfer-Encoding", "chunked"],
             ["Connection", "close, X-Local"], ["X-Local", "remove"], ["Authorization", "Bearer dummy"],
             ["X-Quoted", "a \"quote\" and \\slash"], ["X-Empty", ""]], 2, "e30=", "{}");
        var command = CurlReplay.CreateCommand(request, "{}"u8, Path.GetTempPath());
        var headers = CurlReplay.Headers(request);
        Check.True(command.Contains("https://62.106.75.243:7900/api/v1/pakkey?q=a%20b&n={x}"), "Original destination/path changed.");
        Check.True(headers.Contains("Authorization: Bearer dummy") && command.Contains("--data-binary '{}'"), "Authentication or inline payload missing.");
        Check.True(!headers.Any(value => value.StartsWith("Host:") || value.StartsWith("Content-Length:")
            || value.StartsWith("Transfer-Encoding:") || value.StartsWith("X-Local:")), "Local/transport headers leaked into replay.");
        Check.True(headers.Contains("X-Empty;") && headers.Contains("X-Quoted: a \"quote\" and \\slash") && command.Contains("headers.txt"), "Complex header handling failed.");
        Check.True(command.StartsWith("curl.exe --disable --insecure ", StringComparison.Ordinal)
            && !command.Contains("--curl-pin") && !command.Contains("--pinnedpubkey")
            && !command.Contains("--location") && command.Contains("--proto '=https'"), "Expected plain development curl command with --insecure.");
        Check.Throws<ArgumentException>(() => CurlReplay.CreateCommand(request with { Target = "https://unrelated.example/" }, "{}"u8, Path.GetTempPath()));
        return Task.CompletedTask;
    }

    public static async Task VerifyCaptureAndReplayAsync(WorkspacePaths paths, string serverAddress)
    {
        using var certificate = X509CertificateLoader.LoadCertificateFromFile(paths.Certificate);
        // Export to an isolated local origin for this test. No original-server contact occurs.
        var capture = new DebugRequestCapture(paths, new Uri(serverAddress));
        await using var capturingServer = ServerApplication.Build(paths, 0, new SilentJournal(), capture);
        await capturingServer.StartAsync();
        try
        {
            var address = capturingServer.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var client = new HttpClient(PinnedHttpsProbe.CreateHandler(certificate)) { BaseAddress = new Uri(address) };
            var payload = new byte[] { 0, 255, 128, 13, 10, 34, 92, 38, 36, 40, 41 };
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/pakkey?q=a%20b&n={x}")
            { Content = new ByteArrayContent(payload) };
            request.Headers.Add("Authorization", "Bearer dummy-secret-for-test");
            request.Headers.Add("X-Rehost-Session", "test-session");
            request.Headers.TryAddWithoutValidation("X-Quoted", "a \"quote\" and \\slash");
            using var response = await client.SendAsync(request);
            Check.Equal(HttpStatusCode.OK, response.StatusCode);
            var directory = Path.Combine(capture.SessionDirectory, "000001");
            Check.True(payload.AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(directory, "body.bin"))), "Captured bytes changed.");
            var json = File.ReadAllText(Path.Combine(directory, "request.json"));
            Check.True(json.Contains("dummy-secret-for-test") && json.Contains("test-session"), "Private capture lost headers.");
            Check.True(!File.Exists(Path.Combine(directory, "replay.ps1")) && !File.Exists(Path.Combine(directory, "replay.curl")), "Replay files should not be created.");
            using var savedCapture = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "capture.json")));
            var command = savedCapture.RootElement.GetProperty("curl_command").GetString()!;
            Check.True(command.Contains("body.bin") && command.Contains("headers.txt"), "Exact binary/header data references missing.");

            if (OperatingSystem.IsWindows())
            {
                var echo = await RunCommandAsync(command, paths);
                Check.True(payload.AsSpan().SequenceEqual(Convert.FromBase64String(echo.GetProperty("body_base64").GetString()!)), "Replayed bytes changed.");
                Check.Equal("POST", echo.GetProperty("method").GetString());
                Check.True(echo.GetProperty("headers").EnumerateArray().Any(pair =>
                    pair[0].GetString()!.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
                    && pair[1].GetString() == "Bearer dummy-secret-for-test"), "Replay lost auth header.");
                var simple = new EchoRequest(DateTimeOffset.UtcNow, "POST", "/api/v1/pakkey", "HTTP/1.1",
                    [["Content-Type", "application/json"]], 2, "e30=", "{}");
                var simpleCommand = CurlReplay.CreateCommand(simple, "{}"u8, directory, new Uri(serverAddress));
                Check.True(!simpleCommand.Contains("body.bin") && !simpleCommand.Contains("headers.txt"), "Simple request should be self-contained.");
                var simpleEcho = await RunCommandAsync(simpleCommand, paths);
                Check.Equal("{}", simpleEcho.GetProperty("body_text").GetString());
            }
            using var socket = new ClientWebSocket();
            socket.Options.RemoteCertificateValidationCallback = (_, cert, _, errors) =>
                PinnedHttpsProbe.ValidateCertificate(cert, certificate, LocalCertificate.Fingerprint(certificate), errors);
            using var wsTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await socket.ConnectAsync(new Uri(address.Replace("https://", "wss://", StringComparison.Ordinal) + "/api/v1/game"), wsTimeout.Token);
            var binary = new byte[] { 255, 0, 1, 2 };
            await socket.SendAsync(binary.AsMemory(), WebSocketMessageType.Binary, true, wsTimeout.Token);
            var buffer = new byte[32];
            var frame = await socket.ReceiveAsync(buffer.AsMemory(), wsTimeout.Token);
            Check.Equal(binary.Length, frame.Count);
            Check.True(binary.AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(capture.SessionDirectory, "000002", "websocket", "000001.bin"))), "WebSocket payload was not captured.");
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", wsTimeout.Token);
        }
        finally { await capturingServer.StopAsync(); }
    }

    private sealed class SilentJournal : IRequestJournal
    {
        public void Write(RequestMetadata metadata) { }
    }

    private static async Task<JsonElement> RunCommandAsync(string command, WorkspacePaths paths)
    {
        // Tests use a local mock origin only. Append its CA and hide response headers for JSON assertions.
        var info = new ProcessStartInfo("powershell.exe")
        {
            WorkingDirectory = paths.Root,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[] { "-NoProfile", "-Command", command + " --cacert '"
            + paths.Certificate.Replace("'", "''", StringComparison.Ordinal) + "' --no-include; exit $LASTEXITCODE" })
            info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { if (!process.HasExited) process.Kill(); throw; }
        var output = await stdout;
        Check.True(process.ExitCode == 0, "Local copy-paste curl command failed: " + output + await stderr);
        using var json = JsonDocument.Parse(output);
        return json.RootElement.Clone();
    }
}
