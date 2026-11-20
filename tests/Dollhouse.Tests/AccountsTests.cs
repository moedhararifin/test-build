using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Dollhouse.Server.Accounts;
using Dollhouse.Server.Debugging;
using Dollhouse.Server.Echo;
using Dollhouse.Server.Endpoints;
using Dollhouse.Server.Modules;
using Dollhouse.Server.Requests;

namespace Dollhouse.Tests;

internal static class AccountsTests
{
    public static async Task VerifyAsync()
    {
        using var workspace = new TemporaryWorkspace();
        var accounts = new LocalAccountStore(workspace.Paths);
        var first = accounts.Login("First", "profile-fixture")!;
        var second = accounts.Login("Second", "other-fixture")!;
        var profiles = new LocalProfileStore(workspace.Paths);
        using var services = Services(accounts, profiles);
        using (var empty = await Send(services, "GET", AccountsEndpoint.Path, [first.Session]))
            Check.Equal(0, empty.RootElement.GetProperty("accounts").GetArrayLength());

        const string payload = "+/8=";
        var upload = new ProfileUpload("Local 欢迎 +", 1794528000, "p2", new() { ["Progress.txt"] = payload });
        var body = JsonSerializer.Serialize(upload, Dollhouse.Core.JsonOutput.Options);
        foreach (var sessions in new[] { Array.Empty<string>(), new[] { "invalid" }, new[] { first.Session, second.Session } })
        {
            using var rejectedGet = await Send(services, "GET", AccountsEndpoint.Path, sessions, status: 401);
            using var rejectedPost = await Send(services, "POST", AccountsEndpoint.Path, sessions, body, 401);
        }
        Check.Equal(0, profiles.List(first.User.Id).Length);
        using var created = await Send(services, "POST", AccountsEndpoint.Path, [first.Session, first.Session], body);
        var id = created.RootElement.GetProperty("id").GetString()!;
        Check.True(Guid.TryParseExact(id, "N", out _), "Profile ID is not stable path-safe metadata.");
        Check.Equal(3, created.RootElement.EnumerateObject().Count());
        Check.Equal(1, created.RootElement.GetProperty("rev").GetInt32());
        Check.Equal(1, created.RootElement.GetProperty("epoch").GetInt32());

        using (var listed = await Send(services, "GET", AccountsEndpoint.Path, [first.Session]))
        {
            var records = listed.RootElement.GetProperty("accounts");
            Check.Equal(1, records.GetArrayLength());
            Check.Equal(id, records[0].GetProperty("id").GetString());
            Check.True(!records[0].TryGetProperty("files", out _) && !records[0].TryGetProperty("owner", out _), "List exposed private snapshot/owner fields.");
        }
        using (var detail = await Send(services, "GET", AccountsEndpoint.Path + "/" + id, [first.Session]))
        {
            Check.Equal(upload.Name, detail.RootElement.GetProperty("name").GetString());
            Check.Equal(upload.Created, detail.RootElement.GetProperty("created").GetInt64());
            Check.Equal(payload, detail.RootElement.GetProperty("files").GetProperty("Progress.txt").GetString());
        }
        using var hidden = await Send(services, "GET", AccountsEndpoint.Path + "/" + id, [second.Session], status: 404);
        using var unknown = await Send(services, "GET", AccountsEndpoint.Path + "/unknown", [first.Session], status: 404);
        using var anonymousDetail = await Send(services, "GET", AccountsEndpoint.Path + "/" + id, [], status: 401);
        using var secondList = await Send(services, "GET", AccountsEndpoint.Path, [second.Session]);
        Check.Equal(0, secondList.RootElement.GetProperty("accounts").GetArrayLength());

        // Retried creations must survive concurrency/restart without making duplicates.
        var retries = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            Send(services, "POST", AccountsEndpoint.Path, [first.Session], body))));
        foreach (var retry in retries) { using (retry) Check.Equal(id, retry.RootElement.GetProperty("id").GetString()); }
        Check.Equal(1, profiles.List(first.User.Id).Length);
        var snapshotPath = System.IO.Path.Combine(workspace.Paths.Local, "profiles", id + ".json");
        var original = File.ReadAllBytes(snapshotPath);
        using var conflict = await Send(services, "POST", AccountsEndpoint.Path, [first.Session],
            JsonSerializer.Serialize(upload with { Name = "Changed" }, Dollhouse.Core.JsonOutput.Options), 409);
        Check.True(original.AsSpan().SequenceEqual(File.ReadAllBytes(snapshotPath)), "Conflicting create overwrote the existing snapshot.");

        using var otherOwner = await Send(services, "POST", AccountsEndpoint.Path, [second.Session], body);
        Check.True(id != otherOwner.RootElement.GetProperty("id").GetString(), "Different users share a profile ID.");
        using var newGeneration = await Send(services, "POST", AccountsEndpoint.Path, [first.Session],
            JsonSerializer.Serialize(upload with { Created = upload.Created + 1 }, Dollhouse.Core.JsonOutput.Options));
        Check.True(id != newGeneration.RootElement.GetProperty("id").GetString(), "Recreated profile reused the earlier generation's snapshot.");

        foreach (var invalid in new[] { "", "[]", "{}", "{", body.Replace("1794528000", "\"wrong\""),
            "{\"name\":\"A\",\"created\":1,\"created\":2,\"profile\":\"p2\",\"files\":{}}" })
        { using var rejected = await Send(services, "POST", AccountsEndpoint.Path, [first.Session], invalid, 400); }
        foreach (var files in new Dictionary<string, string>[]
        {
            new() { ["../outside.txt"] = payload }, new() { ["C:\\outside.txt"] = payload },
            new() { ["Progress.txt"] = "bad-base64" }, new() { ["binary.exe"] = payload },
            new() { ["Case.txt"] = payload, ["case.txt"] = payload }
        })
        { using var rejected = await Send(services, "POST", AccountsEndpoint.Path, [first.Session],
            JsonSerializer.Serialize(upload with { Files = files }, Dollhouse.Core.JsonOutput.Options), 400); }
        Check.True(!Directory.EnumerateFiles(workspace.Paths.Root, "*.txt", SearchOption.AllDirectories).Any(), "Uploaded files were materialized.");
        var restarted = new LocalProfileStore(workspace.Paths);
        Check.Equal(payload, restarted.Read(first.User.Id, id)!.Files["Progress.txt"]);
        Check.True(restarted.Read(second.User.Id, id) is null, "Restart leaked another owner's snapshot.");
        Check.Equal(2, restarted.List(first.User.Id).Length);
        Check.Equal(1, restarted.List(second.User.Id).Length);
        Check.True(!Directory.EnumerateFiles(System.IO.Path.GetDirectoryName(snapshotPath)!, "*.tmp").Any(), "Temporary uploads were retained.");
        // Incomplete saves are never treated as committed snapshots. Invalid
        // committed state fails closed and remains intact for recovery.
        var incomplete = snapshotPath + ".interrupted.tmp";
        File.WriteAllText(incomplete, "{");
        Check.Equal(2, new LocalProfileStore(workspace.Paths).List(first.User.Id).Length);
        File.Delete(incomplete);
        var nullUpload = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllBytes(snapshotPath))!;
        nullUpload["upload"] = null;
        File.WriteAllText(snapshotPath, nullUpload.ToJsonString());
        Check.Throws<InvalidDataException>(() => new LocalProfileStore(workspace.Paths));
        File.WriteAllText(snapshotPath, "{\"version\":99}");
        var corrupt = File.ReadAllBytes(snapshotPath);
        Check.Throws<InvalidDataException>(() => new LocalProfileStore(workspace.Paths));
        Check.True(corrupt.AsSpan().SequenceEqual(File.ReadAllBytes(snapshotPath)), "Invalid committed state was overwritten.");
        using var failedWorkspace = new TemporaryWorkspace();
        var failedStore = new LocalProfileStore(failedWorkspace.Paths);
        Directory.Delete(System.IO.Path.Combine(failedWorkspace.Paths.Local, "profiles")); // Empty fixture directory only.
        Check.Throws<IOException>(() => failedStore.Create(first.User.Id, upload));
        Check.Equal(0, failedStore.List(first.User.Id).Length);
        VerifyRouting();
    }

    private static void VerifyRouting()
    {
        var exact = new ServerRoute("GET", "/items/special", (_, _) => Task.CompletedTask);
        var pattern = new ServerRoute("GET", "/items/{id}", (_, _) => Task.CompletedTask);
        var routes = new RequestHandlersModule([pattern, exact]);
        var context = new DefaultHttpContext(); context.Request.Method = "GET";
        context.Request.Path = "/items/special";
        Check.Equal(exact, routes.Match(context));
        context.Request.Path = "/items/one";
        Check.Equal(pattern, routes.Match(context));
        Check.Equal("one", context.Request.RouteValues["id"] as string);
        foreach (var path in new[] { "/items/", "/items/one/claim", "/Items/one" })
        { context.Request.Path = path; Check.True(routes.Match(context) is null, "Parameter route matched extra/missing/different segments."); }
        context.Request.Path = "/items/one"; context.Request.Method = "POST";
        Check.True(routes.Match(context) is null, "Parameter route ignored the request method.");
        Check.Throws<ArgumentException>(() => new RequestHandlersModule([new("GET", "/items/{id}/{ID}", (_, _) => Task.CompletedTask)]));
    }

    private static ServiceProvider Services(LocalAccountStore accounts, LocalProfileStore profiles)
    {
        var endpoint = new AccountsEndpoint(accounts, profiles);
        return new ServiceCollection().AddSingleton(new ServerPipeline(new Journal(), new NoRequestCapture(),
            [endpoint.GetRoute, endpoint.PostRoute, endpoint.DetailRoute])).BuildServiceProvider();
    }

    private static async Task<JsonDocument> Send(IServiceProvider services, string method, string path, string[] sessions,
        string body = "", int status = 200)
    {
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Method = method; context.Request.Path = path; context.Request.Protocol = "HTTP/1.1";
        if (sessions.Length != 0) context.Request.Headers["X-Rehost-Session"] = sessions;
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body)); context.Response.Body = new MemoryStream();
        await RequestDispatcher.HandleAsync(context);
        Check.Equal(status, context.Response.StatusCode);
        var bytes = ((MemoryStream)context.Response.Body).ToArray();
        Check.Equal((long?)bytes.Length, context.Response.ContentLength);
        Check.Equal("no-store", context.Response.Headers.CacheControl.ToString());
        if (path.Length > AccountsEndpoint.Path.Length)
            Check.True(Encoding.UTF8.GetString(bytes).IndexOf("\\u002B", StringComparison.OrdinalIgnoreCase) < 0, "Native detail response escaped plus.");
        return JsonDocument.Parse(bytes);
    }

    private sealed class Journal : IRequestJournal
    {
        public void Write(RequestMetadata request) => Check.Equal(RequestHandling.Api, request.Handling);
    }
}
