namespace Dollhouse.Gameplay;

internal sealed unsafe partial class NativeGame
{
    // Three operations (matching, refresh, area lock). An unchanged failure can
    // affect hundreds of cards in one UI refresh; report it once per operation.
    private readonly Dictionary<string, string> cardFilteringFailures = [];

    internal void LogCardFiltering(string operation, Exception error)
    {
        try
        {
            string failure = error.GetType().Name + ": " + error.Message;
            lock (cardFilteringFailures)
            {
                if (cardFilteringFailures.TryGetValue(operation, out string? previous) && previous == failure) return;
                cardFilteringFailures[operation] = failure;
            }
            fixed (char* message = $"[Dollhouse card filtering] {operation} failed: {failure}")
                ((delegate* unmanaged<char*, void>)At(SupportedBuild.GameplayLog))(message);
        }
        catch { /* Diagnostics must not interrupt native cleanup or cross the export boundary. */ }
    }
}
