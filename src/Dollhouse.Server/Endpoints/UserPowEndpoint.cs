using Dollhouse.Server.Modules;
using Dollhouse.Server.Requests;

namespace Dollhouse.Server.Endpoints;

/// <summary>Use the client's native optional-PoW branch for this loopback-only emulator.</summary>
public sealed class UserPowEndpoint
{
    public const string Path = "/api/v1/user/pow";

    public ServerRoute Route => new("GET", Path, (request, cancellationToken) =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        // No puzzle is issued, so challenge/bits/expiry fields are unnecessary.
        // This is local policy, not a captured original-server response or account approval.
        return JsonResponse.WriteAsync(request.Context, StatusCodes.Status200OK, new { Required = false });
    });
}
