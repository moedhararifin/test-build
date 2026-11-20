using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Dollhouse.Server.Debugging;
using Dollhouse.Server.Echo;
using Dollhouse.Server.Endpoints;
using Dollhouse.Server.Requests;

namespace Dollhouse.Tests;

internal static class ClientReportTests
{
    public static async Task VerifyAsync()
    {
        var journal = new Journal();
        var capture = new Capture();
        using var services = new ServiceCollection().AddSingleton(new ServerPipeline(journal, capture,
            [new ClientReportEndpoint().Route])).BuildServiceProvider();
        // Synthetic lifecycle fixtures from the observed request schema. The native
        // response consumer only requires 200; diagnostics must never be echoed back.
        foreach (var eventName in new[] { "start", "map", "quit" })
        {
            var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            {
                version = "", seq = -1, exe = "fixture", profile = "local", profileName = "Local",
                mode = "Original", map = eventName == "map" ? "Title" : "", @event = eventName,
                account = "PRIVATE_ACCOUNT", sync = true, user = "PRIVATE_USER", delivered = new[] { "fixture-mail" }
            }));
            var response = await Send(services, "POST", body);
            VerifyEmptyResponse(response);
            Check.True(body.AsSpan().SequenceEqual(capture.LastBody), "Report was not available to opt-in capture.");
            Check.Equal(RequestHandling.Api, journal.Last!.Handling);
        }
        // This endpoint does not interpret diagnostics, including absent/future fields
        // or malformed JSON. Existing pipeline limits still bound all request bodies.
        foreach (var body in new[] { Array.Empty<byte>(), "{}"u8.ToArray(), new byte[] { 0xff, 0x00 } })
            VerifyEmptyResponse(await Send(services, "POST", body));
        Check.Equal(6, capture.Count);
        Check.Equal(6, journal.Count);
        Check.True(journal.Last!.Headers.All(pair => !pair.Contains("PRIVATE_TOKEN")), "Normal report logging exposed authorization.");

        var unsupported = await Send(services, "GET", []);
        using var echo = JsonDocument.Parse(((MemoryStream)unsupported.Response.Body).ToArray());
        Check.True(echo.RootElement.TryGetProperty("body_length", out _), "Unobserved report method was registered.");
        Check.Equal(RequestHandling.Echo, journal.Last!.Handling);
    }

    private static async Task<HttpContext> Send(IServiceProvider services, string method, byte[] body)
    {
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Method = method; context.Request.Path = ClientReportEndpoint.Path;
        context.Request.QueryString = new QueryString("?fixture=1"); context.Request.Protocol = "HTTP/1.1";
        context.Request.ContentType = "application/json";
        context.Request.Headers.Authorization = "PRIVATE_TOKEN";
        context.Request.Body = new MemoryStream(body); context.Response.Body = new MemoryStream();
        await RequestDispatcher.HandleAsync(context);
        return context;
    }

    private sealed class Journal : IRequestJournal
    {
        public int Count { get; private set; }
        public RequestMetadata? Last { get; private set; }
        public void Write(RequestMetadata request) { Last = RequestRedactor.Redact(request); Count++; }
    }

    private sealed class Capture : IRequestCapture
    {
        public int Count { get; private set; }
        public byte[] LastBody { get; private set; } = [];
        public Task<RequestCaptureResult?> RecordRequestAsync(EchoRequest request, ReadOnlyMemory<byte> body, CancellationToken token)
        {
            Count++; LastBody = body.ToArray();
            return Task.FromResult<RequestCaptureResult?>(null);
        }
        public Task RecordWebSocketAsync(string? requestId, System.Net.WebSockets.WebSocketMessageType type,
            bool endOfMessage, ReadOnlyMemory<byte> payload, CancellationToken token) => Task.CompletedTask;
    }

    private static void VerifyEmptyResponse(HttpContext response)
    {
        Check.Equal(200, response.Response.StatusCode);
        Check.Equal(0L, ((MemoryStream)response.Response.Body).Length);
        Check.Equal((long?)0, response.Response.ContentLength);
        Check.Equal("no-store", response.Response.Headers.CacheControl.ToString());
        Check.True(string.IsNullOrEmpty(response.Response.ContentType), "Empty report response advertised content.");
    }
}