using Dollhouse.Core.Security;
using Dollhouse.Server.Modules;
using Dollhouse.Server.Requests;

namespace Dollhouse.Server.Endpoints;

public sealed class LicenseKeysetEndpoint(LocalLicenseAuthority authority)
{
    public const string Path = "/api/v1/license/keyset";

    // Verification keys are public. Session/device headers are not used to grant entitlement here.
    public ServerRoute Route => new("POST", Path, (request, cancellationToken) =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        return JsonResponse.WriteAsync(request.Context, 200, authority.Keyset);
    });
}
