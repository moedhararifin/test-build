using Dollhouse.Server.Modules;
using Dollhouse.Server.Requests;

namespace Dollhouse.Server.Endpoints;

public sealed class UserHintEndpoint(TimeProvider? clock = null)
{
    public const string Path = "/api/v1/user/hint";
    private readonly TimeProvider clock = clock ?? TimeProvider.System;

    public ServerRoute Route => new("GET", Path, (request, cancellationToken) =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        return JsonResponse.WriteAsync(request.Context, StatusCodes.Status200OK,
            new { Users = 1337, Now = this.clock.GetUtcNow().ToUnixTimeSeconds() });
    });
}
