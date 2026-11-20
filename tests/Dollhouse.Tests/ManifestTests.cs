using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Dollhouse.Server.Debugging;
using Dollhouse.Server.Echo;
using Dollhouse.Server.Endpoints;
using Dollhouse.Server.Requests;

namespace Dollhouse.Tests;

internal static class ManifestTests
{
    public static async Task VerifyAsync()
    {
        var journal = new Journal();
        using var services = new ServiceCollection().AddSingleton(new ServerPipeline(journal, new NoRequestCapture(),
            [new ManifestEndpoint().Route])).BuildServiceProvider();
        foreach (var query in new[] { "", "?sample=1" })
        {
            var response = await Send(services, "GET", query);
            Check.Equal(404, response.Response.StatusCode);
            Check.Equal("no-store", response.Response.Headers.CacheControl.ToString());
            Check.Equal((long?)((MemoryStream)response.Response.Body).Length, response.Response.ContentLength);
            Check.Equal(0L, ((MemoryStream)response.Response.Body).Length);
            Check.True(string.IsNullOrEmpty(response.Response.ContentType), "Empty manifest response advertised a content type.");
        }
        Check.Equal(2, journal.Handled);
        var unsupported = await Send(services, "POST", "");
        using (var echo = JsonDocument.Parse(((MemoryStream)unsupported.Response.Body).ToArray()))
            Check.True(echo.RootElement.TryGetProperty("body_length", out _), "Unobserved method was registered.");
    }

    private static async Task<HttpContext> Send(IServiceProvider services, string method, string query)
    {
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Method = method; context.Request.Path = ManifestEndpoint.Path;
        context.Request.QueryString = new QueryString(query); context.Request.Protocol = "HTTP/1.1";
        context.Request.Body = new MemoryStream(); context.Response.Body = new MemoryStream();
        await RequestDispatcher.HandleAsync(context);
        return context;
    }
    private sealed class Journal : IRequestJournal
    {
        public int Handled { get; private set; }
        public void Write(RequestMetadata request) { if (request.Handling == RequestHandling.Api) Handled++; }
    }
}
