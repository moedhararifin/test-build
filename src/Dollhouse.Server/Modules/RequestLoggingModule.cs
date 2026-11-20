using Dollhouse.Server.Debugging;
using Dollhouse.Server.Echo;
using Dollhouse.Server.Requests;

namespace Dollhouse.Server.Modules;

public sealed class RequestLoggingModule(IRequestJournal journal, IRequestCapture capture) : IServerModule
{
    private readonly SemaphoreSlim outputGate = new(1, 1);

    public async Task InvokeAsync(ServerRequest request, ServerModuleDelegate next, CancellationToken cancellationToken)
    {
        var message = request.Message;
        var metadata = new RequestMetadata(message.Time, message.Method, message.Target, message.Headers, message.BodyLength,
            request.SelectedRoute is null ? RequestHandling.Echo : RequestHandling.Api);
        // Serialize only capture/logging, not handlers. An asynchronous disk write must
        // not separate one request's main line from its details or reorder capture IDs.
        await outputGate.WaitAsync(cancellationToken);
        try
        {
            RequestCaptureResult? result;
            try { result = await capture.RecordRequestAsync(message, request.Body, cancellationToken); }
            catch { journal.Write(metadata); throw; }
            request.CaptureId = result?.RequestId;
            journal.Write(metadata, result);
        }
        finally { outputGate.Release(); }
        await next(request, cancellationToken);
    }
}
