namespace Dollhouse.Gameplay;

/// <summary>Apply positive volume deltas without exceeding the shared body-fluid capacity.</summary>
public static class CumflationPatch
{
    public const int CapacityAttribute = SupportedBuild.SemenCapacityAttribute;
    public const int DefaultCapacityMl = SupportedBuild.SemenCapacityMl; // Unknown roles only; see SemenCapacity.
    public const int GiAttribute = 0x49;
    public const int WombAttribute = 0x4a;

    public static (int Womb, int Gi) Accumulate(int capacity, int womb, int gi, int addedWomb, int addedGi)
    {
        if (capacity <= 0) return (womb, gi);
        long available = Math.Max(0L, (long)capacity - Math.Max(0, womb) - Math.Max(0, gi));
        int wombGain = (int)Math.Min(available, Math.Max(0, addedWomb));
        available -= wombGain;
        int giGain = (int)Math.Min(available, Math.Max(0, addedGi));
        return (wombGain == 0 ? womb : Math.Max(0, womb) + wombGain,
            giGain == 0 ? gi : Math.Max(0, gi) + giGain);
    }
}
