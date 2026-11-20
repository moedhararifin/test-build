using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Dollhouse.Server.Debugging;
using Dollhouse.Server.Echo;
using Dollhouse.Server.Endpoints;
using Dollhouse.Server.Requests;

namespace Dollhouse.Tests;

internal static class MailEndpointTests
{
    public static async Task VerifyAsync()
    {
        var journal = new Journal();
        using var services = new ServiceCollection().AddSingleton(new ServerPipeline(journal, new NoRequestCapture(),
            [new MailEndpoint().Route])).BuildServiceProvider();
        string? previousBody = null;
        foreach (var query in new[] { "", "?profile=Local&created=1794528000&account=", "?profile=Other&account=Other" })
        {
            var response = await Send(services, "GET", query);
            Check.Equal(200, response.Response.StatusCode);
            Check.Equal("no-store", response.Response.Headers.CacheControl.ToString());
            var bytes = ((MemoryStream)response.Response.Body).ToArray();
            Check.Equal((long?)bytes.Length, response.Response.ContentLength);
            var body = System.Text.Encoding.UTF8.GetString(bytes);
            if (previousBody is not null) Check.Equal(previousBody, body);
            previousBody = body;
            using var json = JsonDocument.Parse(bytes);
            var mails = json.RootElement.GetProperty("mails");
            Check.Equal(0, mails.GetArrayLength());
        }
        Check.Equal(3, journal.Handled);
        var unsupported = await Send(services, "POST", "");
        using var echo = JsonDocument.Parse(((MemoryStream)unsupported.Response.Body).ToArray());
        Check.True(echo.RootElement.TryGetProperty("body_length", out _), "Unobserved mail method was registered.");
    }

    private static async Task<HttpContext> Send(IServiceProvider services, string method, string query)
    {
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Method = method; context.Request.Path = MailEndpoint.Path;
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
