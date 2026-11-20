using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Dollhouse.Server.Accounts;
using Dollhouse.Server.Debugging;
using Dollhouse.Server.Echo;
using Dollhouse.Server.Endpoints;
using Dollhouse.Server.Requests;

namespace Dollhouse.Tests;

internal static class UserEmailTests
{
    public static async Task VerifyAsync()
    {
        using var workspace = new TemporaryWorkspace();
        var accounts = new LocalAccountStore(workspace.Paths);
        var first = accounts.Login("First", "fixture-password")!;
        var second = accounts.Login("Second", "other-fixture-password")!;

        // Existing v1 stores predate this feature and have no email property.
        var file = System.IO.Path.Combine(workspace.Paths.Local, "auth", "accounts.json");
        var legacy = JsonNode.Parse(File.ReadAllBytes(file))!;
        foreach (var record in legacy["accounts"]!.AsArray()) record!.AsObject().Remove("email");
        File.WriteAllText(file, legacy.ToJsonString());
        accounts = new LocalAccountStore(workspace.Paths);
        Check.Equal("", accounts.GetEmail(first.Session));

        var journal = new Journal();
        using var services = Services(accounts, journal);
        VerifyState(await Send(services, "GET", [first.Session]), "");
        VerifyState(await Send(services, "GET", [second.Session]), "");
        foreach (var sessions in new[] { Array.Empty<string>(), new[] { "invalid" }, new[] { first.Session, second.Session } })
        {
            Check.Equal(401, (await Send(services, "GET", sessions)).Response.StatusCode);
            Check.Equal(401, (await Send(services, "POST", sessions, "{\"email\":\"blocked@example.test\"}")).Response.StatusCode);
        }

        const string address = "user+local@example.test";
        var bound = await Send(services, "POST", [first.Session, first.Session],
            "{\"email\":\" user+local@example.test \",\"lang\":\"EN\"}");
        VerifyState(bound, address);
        Check.True(Encoding.UTF8.GetString(((MemoryStream)bound.Response.Body).ToArray()).Contains("user+local"), "Native reader received escaped plus.");
        VerifyState(await Send(services, "GET", [first.Session]), address);
        VerifyState(await Send(services, "GET", [second.Session]), "");
        foreach (var body in new[] { "", "[]", "{}", "{", "{\"email\":2}",
            "{\"email\":\"invalid\"}", "{\"email\":\"A <user@example.test>\"}",
            "{\"email\":\"user@example.test\",\"lang\":2}",
            "{\"email\":\"a@example.test\",\"email\":\"b@example.test\"}" })
            Check.Equal(400, (await Send(services, "POST", [first.Session], body)).Response.StatusCode);
        Check.Equal(413, (await Send(services, "POST", [first.Session], new string('x', 4097))).Response.StatusCode);
        VerifyState(await Send(services, "GET", [first.Session]), address);

        // Concurrent changes to separate accounts must not lose either association.
        var changes = await Task.WhenAll(
            Task.Run(() => Send(services, "POST", [first.Session], "{\"email\":\"updated@example.test\"}")),
            Task.Run(() => Send(services, "POST", [second.Session], "{\"email\":\"second@example.test\",\"lang\":\"PT\"}")));
        VerifyState(changes[0], "updated@example.test");
        VerifyState(changes[1], "second@example.test");
        var restarted = new LocalAccountStore(workspace.Paths);
        using var restartedServices = Services(restarted, journal);
        VerifyState(await Send(restartedServices, "GET", [first.Session]), "updated@example.test");
        VerifyState(await Send(restartedServices, "GET", [second.Session]), "second@example.test");
        Check.Equal(first.Session, restarted.Login("First", "fixture-password")!.Session);
        Check.True(restarted.Login("First", "wrong-password") is null, "Email binding changed password checks.");
        Check.Equal(RequestHandling.Api, journal.Last!.Handling);
        Check.True(journal.Last.Headers.All(pair => !pair.Contains(first.Session)), "Normal journal exposed session.");

        var unsupported = await Send(services, "DELETE", [first.Session]);
        using var echo = JsonDocument.Parse(((MemoryStream)unsupported.Response.Body).ToArray());
        Check.True(echo.RootElement.TryGetProperty("body_length", out _), "Unobserved email method was registered.");
        Check.Equal(RequestHandling.Echo, journal.Last!.Handling);
    }

    private static ServiceProvider Services(LocalAccountStore accounts, Journal journal)
    {
        var endpoint = new UserEmailEndpoint(accounts);
        return new ServiceCollection().AddSingleton(new ServerPipeline(journal, new NoRequestCapture(),
            [endpoint.GetRoute, endpoint.PostRoute])).BuildServiceProvider();
    }

    private static void VerifyState(HttpContext context, string email)
    {
        Check.Equal(200, context.Response.StatusCode);
        Check.Equal("no-store", context.Response.Headers.CacheControl.ToString());
        var bytes = ((MemoryStream)context.Response.Body).ToArray();
        Check.Equal((long?)bytes.Length, context.Response.ContentLength);
        using var json = JsonDocument.Parse(bytes);
        var root = json.RootElement;
        // Synthetic fixture from native parser 0x1454f8060; enum metadata names
        // Unbound/Pending/Bound. No original-service response was fetched.
        Check.Equal(3, root.EnumerateObject().Count());
        Check.Equal(email.Length == 0 ? 0 : 2, root.GetProperty("status").GetInt32());
        Check.Equal(email, root.GetProperty("email").GetString());
        Check.Equal(JsonValueKind.False, root.GetProperty("expired").ValueKind);
    }

    private static async Task<HttpContext> Send(IServiceProvider services, string method, string[] sessions, string body = "")
    {
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Method = method; context.Request.Path = UserEmailEndpoint.Path;
        context.Request.Protocol = "HTTP/1.1"; context.Request.ContentType = "application/json";
        if (sessions.Length != 0) context.Request.Headers["X-Rehost-Session"] = sessions;
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.Response.Body = new MemoryStream();
        await RequestDispatcher.HandleAsync(context);
        return context;
    }

    private sealed class Journal : IRequestJournal
    {
        public RequestMetadata? Last { get; private set; }
        public void Write(RequestMetadata request) => Last = RequestRedactor.Redact(request);
    }
}
