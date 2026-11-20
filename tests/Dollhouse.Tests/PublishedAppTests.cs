using System.Collections.Concurrent;
using System.Diagnostics;
using Dollhouse.Core;
using Dollhouse.Core.Diagnostics;
using Dollhouse.Core.Patching;
using Dollhouse.Core.Security;

namespace Dollhouse.Tests;

internal static class PublishedAppTests
{
    public static async Task RunAsync(string executable, string original)
    {
        using var game = new TemporaryWorkspace();
        using var unrelatedWorkingDirectory = new TemporaryWorkspace();
        var target = Path.Combine(game.Paths.Root, "Paralogue", "Binaries", "Win64", "Paralogue-Win64-Shipping.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(original, target);
        var localApp = Path.Combine(game.Paths.Root, "Dollhouse.exe");
        File.Copy(executable, localApp);
        var paths = new WorkspacePaths(game.Paths.Root, ".dollhouse");
        string firstHash;
        string firstPin;
        // Only our own published executable is launched. The game file is data.
        using (var first = Start(localApp, unrelatedWorkingDirectory.Paths.Root))
        {
            var lines = await WaitUntilReadyAsync(first);
            try
            {
                Check.True(!File.Exists(GameBinary.BackupPath(target)), "Fresh startup created a binary backup.");
                Check.True(File.Exists(Path.Combine(paths.Local, "config", "Engine.ini")), "Published startup did not extract its embedded game config.");
                using (GameBinary.OpenVerifiedOriginal(target)) { }
                firstPin = LocalCertificate.Fingerprint(paths.Certificate);
                firstHash = GameBinary.Hash(File.ReadAllBytes(target));
                _ = await PinnedHttpsProbe.RunAsync(paths);
                for (var attempt = 0; attempt < 20 && !lines.Any(line => line.Contains("POST")); attempt++)
                    await Task.Delay(50);
                Check.True(lines.Any(line => line.Contains("POST") && line.Contains("/__echo_probe")), "Request was not shown in the app's own console.");
                using var duplicate = Start(localApp, unrelatedWorkingDirectory.Paths.Root);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await duplicate.WaitForExitAsync(timeout.Token);
                Check.Equal(1, duplicate.ExitCode);
                Check.True((await duplicate.StandardError.ReadToEndAsync(timeout.Token)).Contains("already running"), "Duplicate launch was not explained.");
            }
            finally { await StopAsync(first); }
        }
        // Explicit target mode works from outside the game and reuses the certificate without a backup.
        Check.True(!Directory.Exists(Path.Combine(paths.Local, "debug")), "Published default mode wrote debug captures.");
        using var second = Start(executable, unrelatedWorkingDirectory.Paths.Root, "--debug", "--target", target);
        try
        {
            await WaitUntilReadyAsync(second);
            Check.Equal(firstPin, LocalCertificate.Fingerprint(paths.Certificate));
            Check.Equal(firstHash, GameBinary.Hash(File.ReadAllBytes(target)));
            _ = await PinnedHttpsProbe.RunAsync(paths);
            Check.True(Directory.EnumerateFiles(Path.Combine(paths.Local, "debug"), "capture.json", SearchOption.AllDirectories).Any(), "Published debug mode did not capture the request.");
        }
        finally { await StopAsync(second); }
    }

    private static Process Start(string executable, string workingDirectory, params string[] arguments)
    {
        var info = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        // Every published-app test must disable launching the copied game bytes.
        info.ArgumentList.Add("--no-launch");
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        return Process.Start(info) ?? throw new InvalidOperationException("Could not start the published app.");
    }

    private static async Task<ConcurrentQueue<string>> WaitUntilReadyAsync(Process process)
    {
        var lines = new ConcurrentQueue<string>();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        process.OutputDataReceived += (_, line) =>
        {
            if (line.Data is null) return;
            lines.Enqueue(line.Data);
            if (line.Data.Contains("[4/4] Ready")) ready.TrySetResult();
        };
        process.BeginOutputReadLine();
        var exit = process.WaitForExitAsync();
        var completed = await Task.WhenAny(ready.Task, exit, Task.Delay(TimeSpan.FromSeconds(30)));
        if (completed != ready.Task)
        {
            await StopAsync(process);
            throw new InvalidOperationException("Published app did not become ready: " + await process.StandardError.ReadToEndAsync());
        }
        return lines;
    }

    private static async Task StopAsync(Process process)
    {
        if (!process.HasExited) process.Kill();
        await process.WaitForExitAsync();
    }
}
