using Dollhouse.Server.Debugging;
using Dollhouse.Server.Echo;
using Dollhouse.Server.Modules;

namespace Dollhouse.Server.Requests;

public sealed class ServerPipeline
{
    private readonly ServerModuleDelegate dispatch;
    private readonly RequestHandlersModule handlers;

    public ServerPipeline(IRequestJournal journal, IRequestCapture capture, IEnumerable<ServerRoute>? routes = null,
        IEnumerable<IServerModule>? modules = null)
    {
        handlers = new RequestHandlersModule(routes ?? []);
        // Select once before logging; dispatch uses that same route, not a separate URL guess.
        IServerModule[] ordered = [new RequestLoggingModule(journal, capture),
            .. modules ?? [], handlers];
        ServerModuleDelegate next = (request, _) => EchoEndpoint.HandleAsync(request, journal, capture);
        foreach (var module in ordered.Reverse())
        {
            var remaining = next;
            next = (request, token) => module.InvokeAsync(request, remaining, token);
        }
        dispatch = next;
    }

    public Task HandleAsync(ServerRequest request, CancellationToken cancellationToken)
    {
        request.SelectedRoute = handlers.Match(request.Context);
        return dispatch(request, cancellationToken);
    }
}
