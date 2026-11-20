using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Dollhouse.Server.Debugging;
using Dollhouse.Server.Echo;
using Dollhouse.Server.Modules;
using Dollhouse.Server.Requests;

namespace Dollhouse.Tests;

internal static class ServerModuleTests
{
    public static async Task VerifyAsync()
    {
        var journal = new Journal();
        var capture = new Capture();
        var calls = 0;
        var pipeline = new ServerPipeline(journal, capture,
        [
            new ServerRoute("POST", "/implemented", async (request, token) =>
            {
                Interlocked.Increment(ref calls);
                token.ThrowIfCancellationRequested();
                Check.True(request.Body.Span.SequenceEqual(new byte[] { 0, 255, 10, 65 }), "Handler body changed.");
                Check.Equal("captured", request.CaptureId);
                await JsonResponse.WriteAsync(request.Context, 202, new { Handled = true });
            })
        ]);
        using var services = new ServiceCollection().AddSingleton(pipeline).BuildServiceProvider();

        // Exercise the real dispatcher with in-memory HTTP contexts: no sockets or TLS host.
        var handled = Context(services, "POST", "/implemented", [0, 255, 10, 65]);
        handled.Request.QueryString = new QueryString("?variant=1");
        await RequestDispatcher.HandleAsync(handled);
        Check.Equal(202, handled.Response.StatusCode);
        using (var response = JsonDocument.Parse(ResponseBytes(handled)))
        {
            Check.True(response.RootElement.GetProperty("handled").GetBoolean(), "Handler did not own response.");
            Check.True(!response.RootElement.TryGetProperty("body_base64", out _), "Echo overwrote handled response.");
        }
        Check.Equal(1, journal.Requests.Count);
        Check.Equal(RequestHandling.Api, journal.Requests.Single().Handling);
        Check.Equal(1, capture.Requests.Count);
        Check.Equal("/implemented?variant=1", capture.Requests.Single().Target);

        var unknown = Context(services, "POST", "/unknown", [0, 255, 10, 65]);
        await RequestDispatcher.HandleAsync(unknown);
        using (var response = JsonDocument.Parse(ResponseBytes(unknown)))
            Check.Equal("AP8KQQ==", response.RootElement.GetProperty("body_base64").GetString());
        Check.Equal(200, unknown.Response.StatusCode);
        Check.Equal(RequestHandling.Echo, journal.Requests.Last().Handling);

        // Methods and path case remain distinct; queries do not affect route selection.
        await RequestDispatcher.HandleAsync(Context(services, "GET", "/implemented", []));
        await RequestDispatcher.HandleAsync(Context(services, "POST", "/Implemented", []));
        Check.Equal(1, calls);
        Check.Equal(4, journal.Requests.Count);
        Check.Equal(4, capture.Requests.Count);
        var head = Context(services, "HEAD", "/unknown", []);
        await RequestDispatcher.HandleAsync(head);
        Check.Equal(0L, head.Response.Body.Length);
        Check.True(head.Response.ContentLength > 0, "HEAD lost would-be echo length.");

        await Task.WhenAll(Enumerable.Range(0, 12).Select(async _ =>
            await RequestDispatcher.HandleAsync(Context(services, "POST", "/implemented", [0, 255, 10, 65]))));
        Check.Equal(13, calls);
        Check.Equal(17, journal.Requests.Count);
        Check.Equal(17, capture.Requests.Count);

        var order = new List<string>();
        var extensionPipeline = new ServerPipeline(journal, capture, modules:
        [new AroundModule("first", order), new AroundModule("second", order)]);
        var extensionContext = Context(services, "GET", "/extension", []);
        await extensionPipeline.HandleAsync(new ServerRequest(extensionContext,
            new EchoRequest(DateTimeOffset.UtcNow, "GET", "/extension", "HTTP/1.1", [], 0, "", ""), ReadOnlyMemory<byte>.Empty), default);
        Check.Equal("first-in,second-in,second-out,first-out", string.Join(',', order));

        Check.Throws<ArgumentException>(() => new RequestHandlersModule([
            new ServerRoute("POST", "/same", Noop), new ServerRoute("post", "/same", Noop)]));
        Check.Throws<ArgumentException>(() => new RequestHandlersModule([new ServerRoute("GET", "/bad?query", Noop)]));

        // Framing rejection remains before body parsing and dispatch.
        var invalid = Context(services, "POST", "/implemented", []);
        invalid.Request.Headers.TransferEncoding = "chunked";
        invalid.Request.Headers["X-Content-Length"] = "0";
        await RequestDispatcher.HandleAsync(invalid);
        Check.Equal(400, invalid.Response.StatusCode);
        Check.Equal(13, calls);
    }

    private static Task Noop(ServerRequest request, CancellationToken token) => Task.CompletedTask;

    private static DefaultHttpContext Context(IServiceProvider services, string method, string path, byte[] body)
    {
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Method = method;
        context.Request.Path = path;
        context.Request.Protocol = "HTTP/1.1";
        context.Request.Body = new MemoryStream(body);
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static byte[] ResponseBytes(HttpContext context) => ((MemoryStream)context.Response.Body).ToArray();

    private sealed class Journal : IRequestJournal
    {
        public ConcurrentQueue<RequestMetadata> Requests { get; } = new();
        public void Write(RequestMetadata request) => Requests.Enqueue(request);
    }

    private sealed class Capture : IRequestCapture
    {
        public ConcurrentQueue<EchoRequest> Requests { get; } = new();
        public Task<RequestCaptureResult?> RecordRequestAsync(EchoRequest request, ReadOnlyMemory<byte> body, CancellationToken token)
        {
            Check.True(body.Span.SequenceEqual(Convert.FromBase64String(request.BodyBase64)), "Capture body changed.");
            Requests.Enqueue(request);
            return Task.FromResult<RequestCaptureResult?>(new("captured", "fixture", null, null));
        }
        public Task RecordWebSocketAsync(string? id, WebSocketMessageType type, bool end, ReadOnlyMemory<byte> payload,
            CancellationToken token) => Task.CompletedTask;
    }

    private sealed class AroundModule(string name, List<string> order) : IServerModule
    {
        public async Task InvokeAsync(ServerRequest request, ServerModuleDelegate next, CancellationToken token)
        {
            Check.Equal("captured", request.CaptureId);
            order.Add(name + "-in");
            await next(request, token);
            order.Add(name + "-out");
        }
    }
}
