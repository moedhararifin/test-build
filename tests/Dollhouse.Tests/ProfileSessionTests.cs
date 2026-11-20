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

internal static class ProfileSessionTests
{
    private const string SessionA = "11111111111111111111111111111111";
    private const string SessionB = "22222222222222222222222222222222";

    public static async Task VerifyAsync()
    {
        using var workspace = new TemporaryWorkspace();
        var accounts = new LocalAccountStore(workspace.Paths);
        var login = accounts.Login("Profile session test", "fixture-password")!;
        var other = accounts.Login("Other profile owner", "other-fixture")!;
        var store = new LocalProfileStore(workspace.Paths);
        var initial = store.Create(login.User.Id, new("Fixture", 1794528000, "p2", new() { ["Progress.txt"] = "AQI=" })).Account;
        var id = initial.Id;
        using var services = Services(accounts, store);

        foreach (var operation in new[] { "claim", "heartbeat", "push", "release" })
        {
            var body = operation == "push" ? PushBody(SessionA, 1, 1, "AwQ=") : SessionBody(SessionA);
            using var unauthenticated = await Send(services, id, operation, [], body, 401);
            using var conflictingHeaders = await Send(services, id, operation, [login.Session, other.Session], body, 401);
            using var wrongOwner = await Send(services, id, operation, [other.Session], body, 404);
            using var missing = await Send(services, "unknown", operation, [login.Session], body, 404);
        }

        using var claim = await Send(services, id, "claim", [login.Session, login.Session], SessionBody(SessionA));
        Check.Equal(1, claim.RootElement.GetProperty("epoch").GetInt32());
        Check.Equal("", claim.RootElement.GetProperty("took_from").GetString());
        using var claimRetry = await Send(services, id, "claim", [login.Session], SessionBody(SessionA));
        Check.Equal(1, claimRetry.RootElement.GetProperty("epoch").GetInt32());
        for (var index = 0; index < 12; index++)
        {
            using var heartbeat = await Send(services, id, "heartbeat", [login.Session], SessionBody(SessionA));
            VerifyNativeHeartbeat(heartbeat, 1);
        }
        var request = PushBody(SessionA, 1, 1, "+/8=", kind: "upload", conflicts: ["Progress.txt"]);
        using var saved = await Send(services, id, "push", [login.Session], request);
        Check.Equal(2, saved.RootElement.GetProperty("rev").GetInt32());
        Check.Equal(1, saved.RootElement.GetProperty("epoch").GetInt32());
        Check.Equal("+/8=", store.Read(login.User.Id, id)!.Files["Progress.txt"]);
        using var retried = await Send(services, id, "push", [login.Session], request);
        Check.Equal(2, retried.RootElement.GetProperty("rev").GetInt32());
        using var heartbeatAfterSave = await Send(services, id, "heartbeat", [login.Session], SessionBody(SessionA));
        VerifyNativeHeartbeat(heartbeatAfterSave, 1); // Revision advances; epoch MUST stay stable.
        using var conflict = await Send(services, id, "push", [login.Session], PushBody(SessionA, 1, 1, "BQY="), 409);
        Check.Equal("conflict", conflict.RootElement.GetProperty("reason").GetString());
        Check.Equal("+/8=", store.Read(login.User.Id, id)!.Files["Progress.txt"]);

        var reloaded = new LocalProfileStore(workspace.Paths);
        using var restarted = Services(accounts, reloaded);
        using var restoredHeartbeat = await Send(restarted, id, "heartbeat", [login.Session], SessionBody(SessionA));
        VerifyNativeHeartbeat(restoredHeartbeat, 1);
        using var restoredRetry = await Send(restarted, id, "push", [login.Session], request);
        Check.Equal(2, restoredRetry.RootElement.GetProperty("rev").GetInt32());

        var raced = await Task.WhenAll(new[] { "Bwg=", "CQo=" }.Select(value => Task.Run(async () =>
        {
            var context = await Dispatch(restarted, id, "push", [login.Session], PushBody(SessionA, 2, 1, value));
            return context.Response.StatusCode;
        })));
        Check.Equal(1, raced.Count(status => status == 200));
        Check.Equal(1, raced.Count(status => status == 409));
        Check.Equal(3, reloaded.ReadState(login.User.Id, id)!.Account.Rev);
        var expectedBeforeTakeover = reloaded.Read(login.User.Id, id)!.Files["Progress.txt"];
        using var takeover = await Send(restarted, id, "claim", [login.Session], SessionBody(SessionB));
        Check.Equal(1, takeover.RootElement.GetProperty("epoch").GetInt32());
        Check.True(!takeover.RootElement.GetRawText().Contains(SessionA), "Claim response exposed the old profile session.");
        using var kicked = await Send(restarted, id, "heartbeat", [login.Session], SessionBody(SessionA));
        Check.True(kicked.RootElement.GetProperty("kicked").GetBoolean(), "Old process retained ownership after an explicit takeover.");
        Check.Equal("another_login", kicked.RootElement.GetProperty("reason").GetString());
        using var locked = await Send(restarted, id, "push", [login.Session], PushBody(SessionA, 3, 1, "Cww="), 423);
        using var rollback = await Send(restarted, id, "push", [login.Session], PushBody(SessionB, 3, 2, "Cww="), 410);
        Check.Equal("rollback", rollback.RootElement.GetProperty("reason").GetString());
        Check.Equal(expectedBeforeTakeover, reloaded.Read(login.User.Id, id)!.Files["Progress.txt"]);
        using var staleRelease = await Send(restarted, id, "release", [login.Session], SessionBody(SessionA));
        Check.True(!staleRelease.RootElement.GetProperty("released").GetBoolean(), "Stale release removed the new process's claim.");
        using var currentHeartbeat = await Send(restarted, id, "heartbeat", [login.Session], SessionBody(SessionB));
        VerifyNativeHeartbeat(currentHeartbeat, 1);
        using var merged = await Send(restarted, id, "push", [login.Session], PushBody(SessionB, 3, 1, "Cww=", "merge", []));
        Check.Equal(4, merged.RootElement.GetProperty("rev").GetInt32());
        using var released = await Send(restarted, id, "release", [login.Session], SessionBody(SessionB));
        Check.True(released.RootElement.GetProperty("released").GetBoolean(), "Current process was not released.");
        using var releasedAgain = await Send(restarted, id, "release", [login.Session], SessionBody(SessionB));
        Check.True(releasedAgain.RootElement.GetProperty("released").GetBoolean(), "Release retry failed.");
        // Native synchronization releases even when the downloaded files match.
        // The same process's later heartbeat must resume without a new epoch/kick.
        using var resumed = await Send(restarted, id, "heartbeat", [login.Session], SessionBody(SessionB));
        VerifyNativeHeartbeat(resumed, 1);
        using var resumedClaim = await Send(restarted, id, "claim", [login.Session], SessionBody(SessionB));
        Check.Equal(1, resumedClaim.RootElement.GetProperty("epoch").GetInt32());

        foreach (var body in new[] { "", "[]", "{}", "{\"session\":2}",
            "{\"session\":\"one\",\"session\":\"two\"}", "{\"session\":\"bad token\"}" })
        { using var invalid = await Send(restarted, id, "heartbeat", [login.Session], body, 400); }
        foreach (var body in new[] { SessionBody(SessionB), PushBody(SessionB, 4, 2, "not-base64"),
            PushBody(SessionB, 4, 2, "AQI=", "unknown"), PushBody(SessionB, 0, 2, "AQI="),
            PushBody(SessionB, 4, 2, "AQI=").Replace("\"base\": 4", "\"base\": \"wrong\"") })
        { using var invalid = await Send(restarted, id, "push", [login.Session], body, 400); }

        var finalStore = new LocalProfileStore(workspace.Paths);
        var state = finalStore.ReadState(login.User.Id, id)!;
        Check.Equal(4, state.Account.Rev); Check.Equal(1, state.Account.Epoch);
        Check.Equal("Cww=", state.Upload.Files["Progress.txt"]);
        var history = System.IO.Path.Combine(workspace.Paths.Local, "profiles", "history");
        var archived = Directory.GetFiles(history, "*.json");
        Check.True(archived.Length >= 4, "Prior snapshots/session states were not archived.");
        Check.True(archived.Any(file => File.ReadAllText(file).Contains("AQI=")), "Original profile bytes were not preserved in history.");

        // A stored v1 profile predating this feature has no active-session fields.
        var legacy = finalStore.Create(login.User.Id, new("Legacy", 1794528001, "p3", new())).Account;
        var legacyPath = System.IO.Path.Combine(workspace.Paths.Local, "profiles", legacy.Id + ".json");
        var legacyJson = JsonNode.Parse(File.ReadAllText(legacyPath))!.AsObject();
        foreach (var field in new[] { "active_session", "released_session", "last_push_fingerprint" }) legacyJson.Remove(field);
        File.WriteAllText(legacyPath, legacyJson.ToJsonString());
        var migrated = new LocalProfileStore(workspace.Paths);
        using var migratedServices = Services(accounts, migrated);
        using var migratedHeartbeat = await Send(migratedServices, legacy.Id, "heartbeat", [login.Session], SessionBody(SessionA));
        VerifyNativeHeartbeat(migratedHeartbeat, 1);
        VerifyMutationFailure(workspace.Paths, migrated, login.User.Id, legacy.Id);
    }

    private static void VerifyMutationFailure(Dollhouse.Core.WorkspacePaths paths, LocalProfileStore store, string owner, string id)
    {
        var file = System.IO.Path.Combine(paths.Local, "profiles", id + ".json");
        var previous = File.ReadAllBytes(file);
        File.Delete(file); // Private temporary test fixture only.
        Check.Throws<IOException>(() => store.Push(owner, id, new(SessionA, 1, 1, "p3", new() { ["Progress.txt"] = "AQI=" })));
        Check.Equal(1, store.ReadState(owner, id)!.Account.Rev);
        Check.Equal(0, store.ReadState(owner, id)!.Upload.Files.Count);
        File.WriteAllBytes(file, previous);
        if (OperatingSystem.IsWindows())
        {
            // A failed history read or final rename must leave disk and memory unchanged.
            foreach (var sharing in new[] { FileShare.None, FileShare.Read })
            {
                using (var locked = new FileStream(file, FileMode.Open, FileAccess.Read, sharing))
                {
                    var error = Check.Throws<Exception>(() => store.Push(owner, id,
                        new(SessionA, 1, 1, "p3", new() { ["Progress.txt"] = "AQI=" })));
                    Check.True(error is IOException or UnauthorizedAccessException, "Expected a file-access failure.");
                }
                Check.True(previous.SequenceEqual(File.ReadAllBytes(file)), "Failed save changed the current snapshot.");
                Check.Equal(1, store.ReadState(owner, id)!.Account.Rev);
                Check.Equal(0, store.ReadState(owner, id)!.Upload.Files.Count);
                Check.Equal(0, Directory.GetFiles(System.IO.Path.GetDirectoryName(file)!, "*.tmp", SearchOption.AllDirectories).Length);
            }
        }
        // The same update can be retried and reloaded after a failed save.
        store.Push(owner, id, new(SessionA, 1, 1, "p3", new() { ["Progress.txt"] = "AQI=" }));
        var reloaded = new LocalProfileStore(paths).ReadState(owner, id)!;
        Check.Equal(2, reloaded.Account.Rev);
        Check.Equal("AQI=", reloaded.Upload.Files["Progress.txt"]);
        var history = System.IO.Path.Combine(paths.Local, "profiles", "history");
        Check.True(Directory.GetFiles(history, "*.json").Any(archive => previous.SequenceEqual(File.ReadAllBytes(archive))),
            "Prior snapshot was not preserved byte-for-byte.");
    }

    private static void VerifyNativeHeartbeat(JsonDocument response, int expectedEpoch)
    {
        Check.Equal(JsonValueKind.False, response.RootElement.GetProperty("kicked").ValueKind);
        Check.Equal(expectedEpoch, response.RootElement.GetProperty("epoch").GetInt32());
    }

    private static string SessionBody(string session) => JsonResponse.Serialize(new { Session = session });
    private static string PushBody(string session, int revision, int epoch, string content, string? kind = null, string[]? conflicts = null)
    {
        var body = new Dictionary<string, object>
        {
            ["session"] = session, ["base"] = revision, ["epoch"] = epoch,
            ["profile"] = "p2", ["files"] = new Dictionary<string, string> { ["Progress.txt"] = content }
        };
        if (kind is not null) body["kind"] = kind;
        if (conflicts is not null) body["conflicts"] = conflicts;
        return JsonResponse.Serialize(body);
    }

    private static ServiceProvider Services(LocalAccountStore accounts, LocalProfileStore profiles) =>
        new ServiceCollection().AddSingleton(new ServerPipeline(new Journal(), new NoRequestCapture(),
            new AccountsEndpoint(accounts, profiles).Routes)).BuildServiceProvider();

    private static async Task<JsonDocument> Send(IServiceProvider services, string id, string operation,
        string[] sessions, string body, int status = 200)
    {
        var context = await Dispatch(services, id, operation, sessions, body);
        Check.Equal(status, context.Response.StatusCode);
        var bytes = ((MemoryStream)context.Response.Body).ToArray();
        Check.Equal((long?)bytes.Length, context.Response.ContentLength);
        Check.Equal("no-store", context.Response.Headers.CacheControl.ToString());
        return JsonDocument.Parse(bytes);
    }

    private static async Task<HttpContext> Dispatch(IServiceProvider services, string id, string operation, string[] sessions, string body)
    {
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Method = "POST"; context.Request.Path = AccountsEndpoint.Path + "/" + id + "/" + operation;
        context.Request.Protocol = "HTTP/1.1";
        if (sessions.Length != 0) context.Request.Headers["X-Rehost-Session"] = sessions;
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body)); context.Response.Body = new MemoryStream();
        await RequestDispatcher.HandleAsync(context);
        return context;
    }

    private sealed class Journal : IRequestJournal
    {
        public void Write(RequestMetadata metadata) => Check.Equal(RequestHandling.Api, metadata.Handling);
    }
}
