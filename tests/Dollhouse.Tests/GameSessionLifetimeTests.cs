using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Dollhouse.App;

namespace Dollhouse.Tests;

internal static class GameSessionLifetimeTests
{
    public static async Task VerifyAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        foreach (var manualStop in new[] { false, true })
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = false,
                RedirectStandardInput = true
            };
            if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(typeof(GameSessionLifetimeTests).Assembly.Location);
            start.ArgumentList.Add("--lifetime-child");
            using var child = Process.Start(start)!;
            using var lifetime = new Lifetime();
            try
            {
                var waiting = GameSessionLifetime.WaitAsync(child, lifetime);
                if (manualStop)
                {
                    lifetime.StopApplication();
                    await waiting.WaitAsync(timeout.Token);
                    Check.True(!child.HasExited, "Stopping the server must not kill the game.");
                }
                await child.StandardInput.WriteLineAsync("exit");
                await child.WaitForExitAsync(timeout.Token);
                await waiting.WaitAsync(timeout.Token);
                Check.Equal(1, lifetime.StopRequests);
                Check.True(lifetime.ApplicationStopping.IsCancellationRequested, "Game exit must stop the server.");
            }
            finally
            {
                if (!child.HasExited) child.Kill(entireProcessTree: true);
            }
        }
    }

    private sealed class Lifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource stopping = new();
        public int StopRequests { get; private set; }
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => stopping.Token;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { StopRequests++; stopping.Cancel(); }
        public void Dispose() => stopping.Dispose();
    }
}
