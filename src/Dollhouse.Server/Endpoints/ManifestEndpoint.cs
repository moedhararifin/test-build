using Dollhouse.Server.Modules;
using Dollhouse.Server.Requests;

namespace Dollhouse.Server.Endpoints;

public sealed class ManifestEndpoint
{
    public const string Path = "/api/v1/manifest";
    public ServerRoute Route => new("GET", Path, (request, cancellationToken) =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Deliberately publish no manifest or download instructions. The client may
        // report a failed update check; no updater-specific game patches are needed.
        request.Context.Response.StatusCode = 404;
        request.Context.Response.ContentLength = 0;
        request.Context.Response.Headers.CacheControl = "no-store";
        return Task.CompletedTask;
    });
}
