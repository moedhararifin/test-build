using Dollhouse.Server.Accounts;
using Dollhouse.Server.Modules;
using Dollhouse.Server.Requests;

namespace Dollhouse.Server.Endpoints;

public sealed class UserMeEndpoint(LocalAccountStore accounts, TimeProvider? clock = null)
{
    public const string Path = "/api/v1/user/me";
    private readonly TimeProvider clock = clock ?? TimeProvider.System;

    public ServerRoute Route => new("GET", Path, (request, cancellationToken) =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        var login = LocalSession.Resolve(request.Context.Request.Headers, accounts);
        return login is null
            ? JsonResponse.WriteAsync(request.Context, 401,
                new { Error = "Local login required.", Reason = "no_login", Now = this.clock.GetUtcNow().ToUnixTimeSeconds() })
            : JsonResponse.WriteAsync(request.Context, 200, login);
    });
}
