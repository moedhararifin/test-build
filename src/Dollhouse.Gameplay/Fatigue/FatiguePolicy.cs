namespace Dollhouse.Gameplay;

internal readonly record struct StaminaState(int Current, int Maximum)
{
    internal bool IsValid => Maximum > 0 && Current >= 0 && Current <= Maximum;
}

internal static class FatiguePolicy
{
    // OFF skips spending, while negative recovery deltas remain valid.
    internal static int EffectiveCost(bool enabled, int cost) => enabled || cost < 0 ? cost : 0;
    internal static bool CannotAfford(bool enabled, int current, int cost) => enabled && current < Math.Max(0, cost);
}
