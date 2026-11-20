using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Dollhouse.Server.Accounts;
using Dollhouse.Server.Debugging;
using Dollhouse.Server.Echo;
using Dollhouse.Server.Endpoints;
using Dollhouse.Server.Requests;

namespace Dollhouse.Tests;

internal static class ShopEndpointTests
{
    public static async Task VerifyAsync()
    {
        using var workspace = new TemporaryWorkspace();
        var accounts = new LocalAccountStore(workspace.Paths);
        var login = accounts.Login("Shop fixture", "fixture-password")!;
        var other = accounts.Login("Other fixture", "another-password")!;
        var clock = new Clock();
        var endpoint = new ShopEndpoint(accounts, clock);
        var journal = new Journal();
        using var services = new ServiceCollection().AddSingleton(new ServerPipeline(journal, new NoRequestCapture(),
            [endpoint.PricesRoute, endpoint.OpenRoute])).BuildServiceProvider();

        // Static contract: 0x145528300 requires two objects and iterates SKU values;
        // campaign is optional. Empty maps deliberately publish no local offers.
        var prices = await Send(services, "POST", ShopEndpoint.PricesPath + "?lang=EN");
        using (var json = Read(prices))
        {
            Check.Equal(3, json.RootElement.EnumerateObject().Count());
            foreach (var field in new[] { "prices", "base" })
            {
                Check.Equal(JsonValueKind.Object, json.RootElement.GetProperty(field).ValueKind);
                Check.Equal(0, json.RootElement.GetProperty(field).EnumerateObject().Count());
            }
            Check.Equal(JsonValueKind.Null, json.RootElement.GetProperty("campaign").ValueKind);
        }
        foreach (var sessions in new[] { new[] { login.Session }, new[] { login.Session, login.Session }, new[] { other.Session } })
            VerifyNoOrder(await Send(services, "POST", ShopEndpoint.OpenPath, sessions), clock.Now);
        clock.Now = clock.Now.AddMinutes(1);
        VerifyNoOrder(await Send(services, "POST", ShopEndpoint.OpenPath, [login.Session]), clock.Now);
        foreach (var sessions in new[] { Array.Empty<string>(), new[] { "invalid" }, new[] { login.Session, other.Session } })
        {
            var unauthorized = await Send(services, "POST", ShopEndpoint.OpenPath, sessions);
            Check.Equal(401, unauthorized.Response.StatusCode);
            using var json = Read(unauthorized);
            Check.Equal("no_login", json.RootElement.GetProperty("reason").GetString());
            Check.True(!json.RootElement.TryGetProperty("order", out _), "Unauthorized request returned order state.");
        }
        foreach (var path in new[] { ShopEndpoint.PricesPath, ShopEndpoint.OpenPath })
        {
            foreach (var body in new[] { "", "{", "[]", "null", "\"string\"", "1" })
                Check.Equal(400, (await Send(services, "POST", path, [login.Session], body)).Response.StatusCode);
            Check.Equal(413, (await Send(services, "POST", path, [login.Session], new string('x', 4097))).Response.StatusCode);
            var wrongMethod = await Send(services, "GET", path);
            using var echo = Read(wrongMethod);
            Check.True(echo.RootElement.TryGetProperty("body_length", out _), "Unobserved GET method was registered.");
        }
        var concurrent = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ =>
            Task.Run(() => Send(services, "POST", ShopEndpoint.OpenPath, [login.Session]))));
        foreach (var context in concurrent) VerifyNoOrder(context, clock.Now);
        Check.True(journal.Events.Where(item => item.Method == "POST").All(item => item.Handling == RequestHandling.Api),
            "Shop POST was logged as echo.");
        Check.True(journal.Events.Where(item => item.Method == "GET").All(item => item.Handling == RequestHandling.Echo),
            "Wrong method was logged as handled.");
        Check.True(journal.Events.All(item => item.Headers.All(header => !header.Contains(login.Session) && !header.Contains(other.Session))),
            "Normal journal leaked a session.");
        Check.True(!Directory.Exists(Path.Combine(workspace.Paths.Local, "shop")), "Empty shop created payment state.");
    }

    private static void VerifyNoOrder(HttpContext context, DateTimeOffset now)
    {
        Check.Equal(200, context.Response.StatusCode);
        using var json = Read(context);
        Check.Equal(2, json.RootElement.EnumerateObject().Count());
        Check.Equal(JsonValueKind.Null, json.RootElement.GetProperty("order").ValueKind);
        Check.Equal(now.ToUnixTimeSeconds(), json.RootElement.GetProperty("now").GetInt64());
    }

    private static JsonDocument Read(HttpContext context)
    {
        var bytes = ((MemoryStream)context.Response.Body).ToArray();
        Check.Equal("application/json; charset=utf-8", context.Response.ContentType);
        Check.Equal("no-store", context.Response.Headers.CacheControl.ToString());
        Check.Equal((long?)bytes.Length, context.Response.ContentLength);
        return JsonDocument.Parse(bytes);
    }

    private static async Task<HttpContext> Send(IServiceProvider services, string method, string target, string[]? sessions = null, string body = "{}")
    {
        var context = new DefaultHttpContext { RequestServices = services };
        var parts = target.Split('?', 2);
        context.Request.Method = method; context.Request.Path = parts[0]; context.Request.Protocol = "HTTP/1.1";
        if (parts.Length > 1) context.Request.QueryString = new QueryString("?" + parts[1]);
        if (sessions is not null) context.Request.Headers["X-Rehost-Session"] = sessions;
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body)); context.Response.Body = new MemoryStream();
        await RequestDispatcher.HandleAsync(context);
        return context;
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Journal : IRequestJournal
    {
        public ConcurrentQueue<RequestMetadata> Events { get; } = new();
        public void Write(RequestMetadata request) => Events.Enqueue(RequestRedactor.Redact(request));
    }
}
