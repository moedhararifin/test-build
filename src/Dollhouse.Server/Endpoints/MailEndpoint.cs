using Dollhouse.Server.Modules;
using Dollhouse.Server.Requests;

namespace Dollhouse.Server.Endpoints;

public sealed class MailEndpoint
{
    public const string Path = "/api/v1/mail";

    public ServerRoute Route => new("GET", Path, (request, cancellationToken) =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        return JsonResponse.WriteAsync(request.Context, StatusCodes.Status200OK, new { Mails = Array.Empty<object>() });
    });
}
