namespace Dollhouse.Gameplay;

// Presentation only: stored volumes and room statistics already contain the final value.
internal sealed class BulgeGrowth(double from, double to)
{
    internal const double DurationSeconds = 2;
    private readonly double start = from, target = to;
    private double elapsed;
    internal double Value { get; private set; } = from;
    internal bool Complete => elapsed >= DurationSeconds;
    internal double Advance(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds <= 0) return Value;
        elapsed = Math.Min(DurationSeconds, elapsed + seconds);
        Value = elapsed == DurationSeconds ? target : start + (target - start) * (elapsed / DurationSeconds);
        return Value;
    }
}
