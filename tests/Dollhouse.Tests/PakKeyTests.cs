using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Dollhouse.Core;
using Dollhouse.Server.Debugging;
using Dollhouse.Server.Echo;
using Dollhouse.Server.Endpoints;
using Dollhouse.Server.Requests;

namespace Dollhouse.Tests;

internal static class PakKeyTests
{
    public static async Task VerifyAsync()
    {
        // Synthetic fixture only: the real validated key must never enter committed tests.
        // Include '+' and '/' so JSON escaping cannot hide a client compatibility regression.
        var fixture = Convert.ToBase64String(Enumerable.Repeat((byte)0xfb, 32).ToArray());
        var previousResponse = JsonSerializer.Serialize(new { Key = fixture, Now = 1_800_000_000 }, JsonOutput.Options);
        Check.True(previousResponse.Contains("\\u002B"), "Negative control no longer exercises escaped plus signs.");
        Check.True(NativeJsonStringReader.Read(previousResponse, "key") != fixture, "Negative control unexpectedly survived the client's limited escape handling.");
        var clock = new Clock(DateTimeOffset.FromUnixTimeSeconds(1_800_000_000));
        var endpoint = new PakKeyEndpoint(fixture, clock);
        Check.True(endpoint.IsConfigured, "Configured endpoint unavailable.");
        var journal = new Journal();
        var capture = new Capture();
        var pipeline = new ServerPipeline(journal, capture, [endpoint.Route]);
        using var services = new ServiceCollection().AddSingleton(pipeline).BuildServiceProvider();

        foreach (var body in new[] { "{}", "{\"lease\":{\"signed\":\"fixture\"}}" })
        {
            var context = Context(services, "POST", PakKeyEndpoint.Path, body);
            context.Request.QueryString = new QueryString("?variant=1");
            context.Request.Headers.Authorization = "Bearer public";
            await RequestDispatcher.HandleAsync(context);
            Check.Equal(200, context.Response.StatusCode);
            Check.Equal("no-store", context.Response.Headers.CacheControl.ToString());
            Check.Equal("application/json; charset=utf-8", context.Response.ContentType);
            Check.Equal(context.Response.Body.Length, context.Response.ContentLength);
            var wireText = Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray());
            Check.True(NativeJsonStringReader.Read(wireText, "key") == fixture,
                "The native client's string extractor cannot read the key from the actual response bytes.");
            using var response = JsonDocument.Parse(((MemoryStream)context.Response.Body).ToArray());
            Check.Equal(2, response.RootElement.EnumerateObject().Count());
            Check.True(response.RootElement.GetProperty("key").GetString() == fixture, "Returned content key changed.");
            Check.Equal(clock.GetUtcNow().ToUnixTimeSeconds(), response.RootElement.GetProperty("now").GetInt64());
            clock.UtcNow = clock.UtcNow.AddSeconds(30);
        }
        Check.Equal(2, journal.Requests.Count);
        Check.Equal(2, capture.Requests.Count);
        Check.True(capture.Requests.Select(request => request.BodyText).SequenceEqual(new[] { "{}", "{\"lease\":{\"signed\":\"fixture\"}}" }),
            "Handled request payloads were not captured.");
        Check.True(!JsonOutput.Serialize(journal.Requests).Contains(fixture), "Response key reached request journal.");

        var otherMethod = Context(services, "GET", PakKeyEndpoint.Path, "");
        await RequestDispatcher.HandleAsync(otherMethod);
        using (var response = JsonDocument.Parse(((MemoryStream)otherMethod.Response.Body).ToArray()))
        {
            Check.True(!response.RootElement.TryGetProperty("key", out _), "Non-POST request returned a content key.");
            Check.Equal("GET", response.RootElement.GetProperty("method").GetString());
        }

        var missing = new PakKeyEndpoint(null);
        Check.True(!missing.IsConfigured, "Missing key treated as configured.");
        using var unavailableServices = new ServiceCollection()
            .AddSingleton(new ServerPipeline(journal, capture, [missing.Route])).BuildServiceProvider();
        var unavailable = Context(unavailableServices, "POST", PakKeyEndpoint.Path, "{}");
        await RequestDispatcher.HandleAsync(unavailable);
        Check.Equal(503, unavailable.Response.StatusCode);
        using (var response = JsonDocument.Parse(((MemoryStream)unavailable.Response.Body).ToArray()))
        {
            Check.Equal("pakkey_not_configured", response.RootElement.GetProperty("reason").GetString());
            Check.True(!response.RootElement.TryGetProperty("key", out _), "Missing configuration manufactured a key.");
        }

        foreach (var invalid in new[] { "", "not-base64", Convert.ToBase64String(new byte[31]), Convert.ToBase64String(new byte[33]) })
            Check.Throws<ArgumentException>(() => new PakKeyEndpoint(invalid));
    }

    private static DefaultHttpContext Context(IServiceProvider services, string method, string path, string body)
    {
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Method = method;
        context.Request.Path = path;
        context.Request.Protocol = "HTTP/1.1";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.Response.Body = new MemoryStream();
        return context;
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed class Journal : IRequestJournal
    {
        public List<RequestMetadata> Requests { get; } = [];
        public void Write(RequestMetadata request) => Requests.Add(request);
    }

    private sealed class Capture : IRequestCapture
    {
        public List<EchoRequest> Requests { get; } = [];
        public Task<RequestCaptureResult?> RecordRequestAsync(EchoRequest request, ReadOnlyMemory<byte> body, CancellationToken token)
        {
            Requests.Add(request);
            return Task.FromResult<RequestCaptureResult?>(new("captured", "fixture", null, null));
        }
        public Task RecordWebSocketAsync(string? id, WebSocketMessageType type, bool end, ReadOnlyMemory<byte> payload,
            CancellationToken token) => Task.CompletedTask;
    }
}
