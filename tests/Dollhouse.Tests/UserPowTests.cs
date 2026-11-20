using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Dollhouse.Server.Debugging;
using Dollhouse.Server.Echo;
using Dollhouse.Server.Endpoints;
using Dollhouse.Server.Requests;

namespace Dollhouse.Tests;

internal static class UserPowTests
{
    public static async Task VerifyAsync()
    {
        var journal = new Journal();
        using var services = new ServiceCollection()
            .AddSingleton(new ServerPipeline(journal, new NoRequestCapture(), [new UserPowEndpoint().Route]))
            .BuildServiceProvider();
        foreach (var target in new[] { UserPowEndpoint.Path, UserPowEndpoint.Path + "?sample=1" })
        {
            var context = Context(services, "GET", target);
            await RequestDispatcher.HandleAsync(context);
            Check.Equal(200, context.Response.StatusCode);
            Check.Equal("no-store", context.Response.Headers.CacheControl.ToString());
            using var json = JsonDocument.Parse(((MemoryStream)context.Response.Body).ToArray());
            // Native branch at 0x1454ee2b0 only enters the solver when required is true.
            Check.Equal(JsonValueKind.False, json.RootElement.GetProperty("required").ValueKind);
            Check.Equal(1, json.RootElement.EnumerateObject().Count());
        }
        var unimplementedRegistration = Context(services, "POST", "/api/v1/user/register");
        await RequestDispatcher.HandleAsync(unimplementedRegistration);
        using var fallback = JsonDocument.Parse(((MemoryStream)unimplementedRegistration.Response.Body).ToArray());
        Check.Equal("POST", fallback.RootElement.GetProperty("method").GetString());
        Check.True(!fallback.RootElement.TryGetProperty("token", out _), "PoW endpoint fabricated a registration result.");
        Check.Equal(3, journal.Count);
    }

    private static DefaultHttpContext Context(IServiceProvider services, string method, string target)
    {
        var context = new DefaultHttpContext { RequestServices = services };
        var parts = target.Split('?', 2);
        context.Request.Method = method;
        context.Request.Path = parts[0];
        if (parts.Length == 2) context.Request.QueryString = new QueryString("?" + parts[1]);
        context.Request.Protocol = "HTTP/1.1";
        context.Request.Body = new MemoryStream();
        context.Response.Body = new MemoryStream();
        return context;
    }

    private sealed class Journal : IRequestJournal
    {
        public int Count { get; private set; }
        public void Write(RequestMetadata request) => Count++;
    }
}
