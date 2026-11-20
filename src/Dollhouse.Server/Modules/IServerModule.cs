using Dollhouse.Server.Requests;

namespace Dollhouse.Server.Modules;

public delegate Task ServerModuleDelegate(ServerRequest request, CancellationToken cancellationToken);

/// <summary>Observe a request and call next, or handle it and finish the response.</summary>
public interface IServerModule
{
    Task InvokeAsync(ServerRequest request, ServerModuleDelegate next, CancellationToken cancellationToken);
}
