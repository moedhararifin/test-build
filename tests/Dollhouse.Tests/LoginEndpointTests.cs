using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Dollhouse.App;
using Dollhouse.Server.Accounts;
using Dollhouse.Server.Debugging;
using Dollhouse.Server.Endpoints;
using Dollhouse.Server.Requests;

namespace Dollhouse.Tests;

internal static class LoginEndpointTests
{
    public static async Task VerifyAsync()
    {
        VerifyHashCompatibility();
        using var workspace = new TemporaryWorkspace();
        var store = new LocalAccountStore(workspace.Paths);
        using var services = Services(store);
        const string password = "synthetic-local-password!23";
        var previous = Console.Out;
        using var log = new StringWriter();
        string session;
        string id;
        try
        {
            Console.SetOut(log);
            var created = await SendAsync(services, "POST", UserLoginEndpoint.Path,
                JsonSerializer.Serialize(new { name = "LocalPlayer", password }));
            Check.Equal(200, created.Response.StatusCode);
            using var json = Read(created);
            var user = json.RootElement.GetProperty("user");
            id = user.GetProperty("id").GetString()!;
            session = json.RootElement.GetProperty("session").GetString()!;
            Check.True(Guid.TryParseExact(id, "N", out _), "Stable local user identifier missing.");
            Check.Equal("LocalPlayer", user.GetProperty("name").GetString());
            Check.Equal(64, session.Length);
            Check.Equal(2, json.RootElement.EnumerateObject().Count());
            Check.Equal("no-store", created.Response.Headers.CacheControl.ToString());

            var repeat = await SendAsync(services, "POST", UserLoginEndpoint.Path,
                JsonSerializer.Serialize(new { name = " localplayer ", password }));
            using var repeated = Read(repeat);
            Check.True(repeated.RootElement.GetProperty("session").GetString() == session, "Repeat login invalidated cached session.");
            Check.True(repeated.RootElement.GetProperty("user").GetProperty("id").GetString() == id, "Repeat login changed identity.");

            var file = Path.Combine(workspace.Paths.Local, "auth", "accounts.json");
            var persisted = await File.ReadAllBytesAsync(file);
            Check.True(!Encoding.UTF8.GetString(persisted).Contains(password), "Plaintext password was persisted.");
            var wrong = await SendAsync(services, "POST", UserLoginEndpoint.Path,
                "{\"name\":\"LocalPlayer\",\"password\":\"incorrect-fixture\"}");
            Check.Equal(401, wrong.Response.StatusCode);
            using var denied = Read(wrong);
            Check.Equal("wrong", denied.RootElement.GetProperty("reason").GetString());
            var afterRejection = await File.ReadAllBytesAsync(file);
            Check.True(persisted.SequenceEqual(afterRejection), "Wrong password changed account data.");

            var anonymous = await SendAsync(services, "GET", UserMeEndpoint.Path);
            Check.Equal(401, anonymous.Response.StatusCode);
            var invalid = await SendAsync(services, "GET", UserMeEndpoint.Path, session: new string('0', 64));
            Check.Equal(401, invalid.Response.StatusCode);
            var me = await SendAsync(services, "GET", UserMeEndpoint.Path, session: session);
            Check.Equal(200, me.Response.StatusCode);
            using var current = Read(me);
            Check.True(current.RootElement.GetProperty("user").GetProperty("id").GetString() == id, "Session lookup returned another identity.");

            foreach (var body in new[] { "{", "[]", "{}", "{\"name\":1,\"password\":\"x\"}",
                "{\"name\":\" \",\"password\":\"x\"}", "{\"name\":\"x\",\"password\":\"\"}" })
            {
                var bad = await SendAsync(services, "POST", UserLoginEndpoint.Path, body);
                Check.Equal(400, bad.Response.StatusCode);
            }
            var huge = await SendAsync(services, "POST", UserLoginEndpoint.Path, new string(' ', 16 * 1024 + 1));
            Check.Equal(413, huge.Response.StatusCode);

            // Loading a fresh store simulates restarting our server, without a socket/process.
            var reopened = new LocalAccountStore(workspace.Paths);
            using var reopenedServices = Services(reopened);
            var cachedMe = await SendAsync(reopenedServices, "GET", UserMeEndpoint.Path, session: session);
            Check.Equal(200, cachedMe.Response.StatusCode);
            using var restored = Read(cachedMe);
            Check.True(restored.RootElement.GetProperty("user").GetProperty("id").GetString() == id, "Restart lost identity.");

            var simultaneous = await Task.WhenAll(Enumerable.Range(0, 4)
                .Select(_ => Task.Run(() => reopened.Login("RacePlayer", password)!)));
            Check.Equal(1, simultaneous.Select(result => result.User.Id).Distinct().Count());
            Check.Equal(1, simultaneous.Select(result => result.Session).Distinct().Count());
            var other = reopened.Login("OtherPlayer", password)!;
            Check.True(other.User.Id != id && other.Session != session, "Different accounts share identifiers or sessions.");
            using (var state = JsonDocument.Parse(await File.ReadAllBytesAsync(file)))
                Check.Equal(3, state.RootElement.GetProperty("accounts").GetArrayLength());

            // A corrupt store must not silently reset identity/password/session state.
            var invalidState = JsonNode.Parse(File.ReadAllBytes(file))!;
            invalidState["accounts"]![0]!["session"] = null;
            var invalidText = invalidState.ToJsonString();
            File.WriteAllText(file, invalidText);
            Check.Throws<InvalidDataException>(() => new LocalAccountStore(workspace.Paths));
            Check.Equal(invalidText, File.ReadAllText(file));
            await File.WriteAllTextAsync(file, "{corrupt-fixture}");
            Check.Throws<InvalidDataException>(() => new LocalAccountStore(workspace.Paths));
            Check.Equal("{corrupt-fixture}", await File.ReadAllTextAsync(file));
        }
        finally { Console.SetOut(previous); }
        Check.True(!log.ToString().Contains(password) && !log.ToString().Contains(session), "Normal console exposed a credential/session.");
        Check.True(log.ToString().Contains("[API]"), "Login route was not marked implemented.");
    }

    private static void VerifyHashCompatibility()
    {
        using var workspace = new TemporaryWorkspace();
        var directory = Path.Combine(workspace.Paths.Local, "auth");
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "accounts.json");
        // Exercise UTF-8, embedded NUL and HMAC's long-key handling against the old provider.
        var password = "legacy-\u00e9-\u6f22-\U0001F3E0-\0" + new string('x', 100);
        var salt = Enumerable.Range(0, 16).Select(value => (byte)value).ToArray();
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 600_000, HashAlgorithmName.SHA256, 32);
        var id = Guid.NewGuid().ToString("N");
        var session = new string('a', 64);
        File.WriteAllText(file, JsonSerializer.Serialize(new
        {
            version = 1,
            accounts = new[] { new { name = "Legacy", id, salt = Convert.ToBase64String(salt),
                password_hash = Convert.ToBase64String(hash), session } }
        }));
        var original = File.ReadAllBytes(file);
        var store = new LocalAccountStore(workspace.Paths);
        var login = store.Login("Legacy", password)!;
        Check.Equal(id, login.User.Id);
        Check.Equal(session, login.Session);
        Check.True(original.SequenceEqual(File.ReadAllBytes(file)), "Legacy login rewrote account state.");
        Check.True(store.Login("Legacy", password + "!") is null, "Legacy account accepted a wrong password.");

        store.Login("NewFormat", password);
        using var state = JsonDocument.Parse(File.ReadAllBytes(file));
        var account = state.RootElement.GetProperty("accounts").EnumerateArray()
            .Single(value => value.GetProperty("name").GetString() == "NewFormat");
        var expected = Rfc2898DeriveBytes.Pbkdf2(password,
            account.GetProperty("salt").GetBytesFromBase64(), 600_000, HashAlgorithmName.SHA256, 32);
        Check.True(expected.SequenceEqual(account.GetProperty("password_hash").GetBytesFromBase64()),
            "New password hashes differ from the existing v1 format.");
        Check.Equal(login, new LocalAccountStore(workspace.Paths).Login("Legacy", password));
    }

    private static ServiceProvider Services(LocalAccountStore store) => new ServiceCollection()
        .AddSingleton(new ServerPipeline(new TerminalRequestJournal(), new NoRequestCapture(),
            [new UserLoginEndpoint(store).Route, new UserMeEndpoint(store).Route])).BuildServiceProvider();

    private static JsonDocument Read(HttpContext context) => JsonDocument.Parse(((MemoryStream)context.Response.Body).ToArray());

    private static async Task<HttpContext> SendAsync(IServiceProvider services, string method, string path,
        string? body = null, string? session = null)
    {
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Method = method;
        context.Request.Path = path;
        context.Request.Protocol = "HTTP/1.1";
        context.Request.Headers.Authorization = "Bearer public";
        if (session is not null) context.Request.Headers["X-Rehost-Session"] = session;
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body ?? ""));
        context.Response.Body = new MemoryStream();
        await RequestDispatcher.HandleAsync(context);
        return context;
    }
}
