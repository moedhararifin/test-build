namespace Dollhouse.Gameplay;

// Steam derives availability from the result set, independently of selection.
// The other filter groups retain their ordinary selection rules.
internal sealed class CardFilterAvailability
{
    internal HashSet<byte> Characters { get; } = [];
    internal HashSet<byte> Organs { get; } = [];
    internal HashSet<byte> Conditions { get; } = [];

    internal static void Include(HashSet<byte> destination, ReadOnlySpan<byte> values)
    {
        foreach (byte value in values) destination.Add(value);
    }
}
