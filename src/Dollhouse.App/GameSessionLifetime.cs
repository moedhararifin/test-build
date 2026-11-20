using System.Diagnostics;
using Microsoft.Extensions.Hosting;

namespace Dollhouse.App;

internal static class GameSessionLifetime
{
    public static async Task WaitAsync(Process game, IHostApplicationLifetime lifetime)
    {
        try
        {
            await game.WaitForExitAsync(lifetime.ApplicationStopping);
            lifetime.StopApplication();
        }
        catch (OperationCanceledException) when (lifetime.ApplicationStopping.IsCancellationRequested)
        {
            // Manual server shutdown does not terminate the game or wait for it.
        }
    }
}
