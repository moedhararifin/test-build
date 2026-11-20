using Dollhouse.Server.Modules;

namespace Dollhouse.Server.Endpoints;

public sealed class ClientReportEndpoint
{
    public const string Path = "/api/v1/client/report";

    public ServerRoute Route => new("POST", Path, (request, cancellationToken) =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Both native report callers discard the response. The shared transport
        // recognizes HTTP 200; no response JSON or diagnostic processing is needed.
        // Logging and opt-in debug capture already ran before this handler.
        request.Context.Response.StatusCode = StatusCodes.Status200OK;
        request.Context.Response.ContentLength = 0;
        request.Context.Response.Headers.CacheControl = "no-store";
        return Task.CompletedTask;
    });
}
