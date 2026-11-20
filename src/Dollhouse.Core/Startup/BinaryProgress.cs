using System.Diagnostics;

namespace Dollhouse.Core.Startup;

public sealed record BinaryProgress(string Operation, string Path, long BytesProcessed,
    long TotalBytes, TimeSpan Elapsed, bool IsComplete)
{
    public int? Percent => TotalBytes > 0 ? (int)Math.Clamp(BytesProcessed * 100 / TotalBytes, 0, 100) : null;
    public bool IsSlow => Elapsed >= TimeSpan.FromSeconds(15);
}

/// <summary>Reports byte progress and keeps reporting elapsed time during blocked disk operations.</summary>
internal sealed class BinaryProgressReporter : IDisposable
{
    private readonly object gate = new();
    private readonly Action<BinaryProgress>? report;
    private readonly Timer timer;
    private readonly Stopwatch elapsed = new();
    private string operation = "", path = "";
    private long processed, total;
    private TimeSpan lastReport;
    private bool active, disposed;

    public BinaryProgressReporter(Action<BinaryProgress>? report)
    {
        this.report = report;
        timer = new Timer(_ => Heartbeat(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public void Begin(string operation, string path, long total = 0)
    {
        lock (gate)
        {
            this.operation = operation;
            this.path = path;
            this.total = total;
            processed = 0;
            active = true;
            elapsed.Restart();
            Publish(false);
        }
    }

    public void Advance(long count)
    {
        lock (gate)
        {
            processed += count;
            if (elapsed.Elapsed - lastReport >= TimeSpan.FromMilliseconds(200)) Publish(false);
        }
    }

    public void Complete()
    {
        lock (gate)
        {
            elapsed.Stop();
            active = false;
            Publish(true);
        }
    }

    private void Publish(bool complete)
    {
        lastReport = elapsed.Elapsed;
        report?.Invoke(new(operation, path, processed, total, elapsed.Elapsed, complete));
    }

    private void Heartbeat()
    {
        // A failed console write on the timer must not crash the backup worker.
        try { lock (gate) { if (active && !disposed) Publish(false); } }
        catch { }
    }

    public void Dispose()
    {
        lock (gate) { disposed = true; timer.Dispose(); }
    }
}
