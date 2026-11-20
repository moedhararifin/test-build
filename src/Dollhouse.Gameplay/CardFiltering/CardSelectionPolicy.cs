namespace Dollhouse.Gameplay;

internal static class CardSelectionPolicy
{
    // Additional selections narrow the result set. Both participant arrays belong
    // to the same card; a value may occur in either, but every selection must occur.
    internal static bool ContainsEvery(ReadOnlySpan<byte> selected, ReadOnlySpan<byte> first,
        ReadOnlySpan<byte> second = default)
    {
        foreach (byte value in selected)
            if (!first.Contains(value) && !second.Contains(value)) return false;
        return true;
    }
}
