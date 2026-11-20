using Dollhouse.App;
using Dollhouse.Core.Patching;

namespace Dollhouse.Tests;

internal static class BinaryProgressTests
{
    public static void Verify()
    {
        VerifyHeartbeat();
        VerifyConsole();
    }

    private static void VerifyHeartbeat()
    {
        using var pulse = new ManualResetEventSlim();
        using var reporter = new BinaryProgressReporter(update =>
        {
            if (!update.IsComplete && update.Elapsed >= TimeSpan.FromMilliseconds(500)) pulse.Set();
        });
        reporter.Begin("Saving backup to disk", "synthetic-backup");
        Check.True(pulse.Wait(TimeSpan.FromSeconds(5)), "Blocked work stopped elapsed-time reporting.");
        reporter.Complete();
    }

    private static void VerifyConsole()
    {
        using var output = new StringWriter();
        var previous = Console.Out;
        try
        {
            Console.SetOut(output);
            for (var i = 0; i <= 100; i++)
                TerminalOutput.BinaryVerification(new("Checking game", "fixture", i, 100, TimeSpan.FromMilliseconds(i * 10), i == 100));
            TerminalOutput.BinaryVerification(new("Saving backup to disk", "fixture", 0, 0, TimeSpan.FromSeconds(16), false));
            TerminalOutput.BinaryVerification(new("Saving backup to disk", "fixture", 0, 0, TimeSpan.FromSeconds(17), true));
        }
        finally { Console.SetOut(previous); }
        var log = output.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
        Check.True(log.Contains("100%") && log.Contains("taking longer than usual") && log.Contains("16s"), "Progress output omitted percentage or slow-operation context.");
        Check.True(log.Contains("Checking game: fixture"), "Verification progress must identify its file.");
        Check.True(!log.Contains("antivirus", StringComparison.OrdinalIgnoreCase), "Slow-operation message invents a cause.");
        if (Console.IsOutputRedirected)
            Check.True(log.Count(character => character == '\n') <= 8 && !log.Contains('\r'), "Redirected progress is noisy or contains cursor controls.");
    }
}
