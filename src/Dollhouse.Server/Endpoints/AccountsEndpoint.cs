using System.Text.Json;
using Dollhouse.Server.Accounts;
using Dollhouse.Server.Modules;
using Dollhouse.Server.Requests;

namespace Dollhouse.Server.Endpoints;

public sealed class AccountsEndpoint(LocalAccountStore accounts, LocalProfileStore profiles, TimeProvider? clock = null)
{
    public const string Path = "/api/v1/accounts";
    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    public ServerRoute GetRoute => new("GET", Path, GetAsync);
    public ServerRoute PostRoute => new("POST", Path, PostAsync);
    public ServerRoute DetailRoute => new("GET", Path + "/{id}", DetailAsync);
    public IReadOnlyList<ServerRoute> Routes => [GetRoute, PostRoute, DetailRoute,
        OperationRoute("claim"), OperationRoute("heartbeat"), OperationRoute("push"), OperationRoute("release")];

    private ServerRoute OperationRoute(string operation) => new("POST", Path + "/{id}/" + operation,
        (request, cancellationToken) => OperationAsync(request, operation, cancellationToken));

    private Task ErrorAsync(HttpContext context, int status, string reason, string message) =>
        JsonResponse.WriteAsync(context, status, new { Error = message, Reason = reason, Now = clock.GetUtcNow().ToUnixTimeSeconds() });

    private Task GetAsync(ServerRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var login = LocalSession.Resolve(request.Context.Request.Headers, accounts);
        return login is null ? ErrorAsync(request.Context, 401, "no_login", "Local login required.")
            : JsonResponse.WriteAsync(request.Context, 200, new { Accounts = profiles.List(login.User.Id) });
    }

    private Task DetailAsync(ServerRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var login = LocalSession.Resolve(request.Context.Request.Headers, accounts);
        if (login is null) return ErrorAsync(request.Context, 401, "no_login", "Local login required.");
        var id = request.Context.Request.RouteValues["id"] as string;
        var state = id is null ? null : profiles.ReadState(login.User.Id, id);
        return state is null ? ErrorAsync(request.Context, 404, "not_found", "Local profile not found.")
            : JsonResponse.WriteAsync(request.Context, 200, new { state.Account.Id, state.Account.Name, state.Account.Created,
                state.Account.Rev, state.Account.Epoch, state.Upload.Files });
    }

    private async Task PostAsync(ServerRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var login = LocalSession.Resolve(request.Context.Request.Headers, accounts);
        if (login is null)
        {
            await ErrorAsync(request.Context, 401, "no_login", "Local login required.");
            return;
        }
        ProfileUpload upload;
        try
        {
            using var json = RequestJson.ParseObject(request.Body);
            var root = json.RootElement;
            upload = new(RequestJson.String(root, "name"), RequestJson.Int64(root, "created"),
                RequestJson.String(root, "profile"), RequestJson.StringMap(root, "files"));
            if (!LocalProfileStore.ValidUpload(upload)) throw new JsonException();
        }
        catch (JsonException)
        {
            await ErrorAsync(request.Context, 400, "bad_request", "Expected name, created, profile, and a files object containing base64 .txt snapshots.");
            return;
        }
        var result = profiles.Create(login.User.Id, upload);
        if (result.Conflict)
        {
            await ErrorAsync(request.Context, 409, "exists", "This profile already has a different saved snapshot. The existing snapshot was preserved.");
            return;
        }
        await JsonResponse.WriteAsync(request.Context, 200, new { result.Account.Id, result.Account.Rev, result.Account.Epoch });
    }

    private async Task OperationAsync(ServerRequest request, string operation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var login = LocalSession.Resolve(request.Context.Request.Headers, accounts);
        if (login is null)
        {
            await ErrorAsync(request.Context, 401, "no_login", "Local login required.");
            return;
        }
        if (operation != "push" && request.Body.Length > 4096)
        {
            await ErrorAsync(request.Context, 413, "bad_request", "Profile session request is too large.");
            return;
        }
        string session;
        ProfilePush? push = null;
        try
        {
            using var json = RequestJson.ParseObject(request.Body);
            var root = json.RootElement;
            session = RequestJson.String(root, "session");
            if (!LocalProfileStore.ValidProfileSession(session)) throw new JsonException();
            if (operation == "push")
            {
                var revision = RequestJson.Int32(root, "base");
                var epoch = RequestJson.Int32(root, "epoch");
                var profile = RequestJson.String(root, "profile");
                var contents = RequestJson.StringMap(root, "files");
                string? kind = null;
                if (root.TryGetProperty("kind", out var kindValue))
                {
                    if (kindValue.ValueKind != JsonValueKind.String) throw new JsonException();
                    kind = kindValue.GetString();
                }
                string[]? conflicts = null;
                if (root.TryGetProperty("conflicts", out var conflictsValue))
                {
                    if (conflictsValue.ValueKind != JsonValueKind.Array || conflictsValue.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String))
                        throw new JsonException();
                    conflicts = conflictsValue.EnumerateArray().Select(item => item.GetString()!).ToArray();
                }
                push = new(session, revision, epoch, profile, contents, kind, conflicts);
                if (!LocalProfileStore.ValidPush(push)) throw new JsonException();
            }
        }
        catch (JsonException)
        {
            await ErrorAsync(request.Context, 400, "bad_request", "Invalid profile session or save request.");
            return;
        }
        var id = request.Context.Request.RouteValues["id"] as string ?? "";
        var result = operation switch
        {
            "claim" => profiles.Claim(login.User.Id, id, session),
            "heartbeat" => profiles.Heartbeat(login.User.Id, id, session),
            "push" => profiles.Push(login.User.Id, id, push!),
            "release" => profiles.Release(login.User.Id, id, session),
            _ => throw new InvalidOperationException("Unknown profile operation.")
        };
        if (result.Status != 200 || result.Account is null)
        {
            await JsonResponse.WriteAsync(request.Context, result.Status, new
            {
                Error = "Profile operation could not be completed.", result.Reason,
                Rev = result.Account?.Rev, Epoch = result.Account?.Epoch, Now = clock.GetUtcNow().ToUnixTimeSeconds()
            });
            return;
        }
        var state = result.Account;
        switch (operation)
        {
            case "claim":
                await JsonResponse.WriteAsync(request.Context, 200, new { state.Id, state.Rev, state.Epoch, result.TookFrom });
                break;
            case "heartbeat":
                await JsonResponse.WriteAsync(request.Context, 200, new { result.Kicked, result.Reason, state.Epoch, state.Rev });
                break;
            case "push":
                await JsonResponse.WriteAsync(request.Context, 200, new { state.Rev, state.Epoch });
                break;
            case "release":
                await JsonResponse.WriteAsync(request.Context, 200, new { result.Released, state.Rev, state.Epoch });
                break;
        }
    }
}
