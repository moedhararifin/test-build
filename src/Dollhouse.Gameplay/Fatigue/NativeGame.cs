namespace Dollhouse.Gameplay;

internal sealed unsafe partial class NativeGame
{
    // Only the synchronous stamina mutation may bypass Progression.txt's mode
    // check. Never change the native global mode or its transaction/defer guards.
    [ThreadStatic] private static NativeGame? staminaWriter;

    internal byte StaminaProgressionMode() => staminaWriter == this
        ? (byte)1 : ((delegate* unmanaged<byte>)At(SupportedBuild.ProfileMode))();

    private StaminaState ReadStamina(GameString* role)
    {
        int current = 0, maximum = 0;
        ((delegate* unmanaged<GameString*, int*, int*, void>)At(SupportedBuild.ReadStoredStamina))(role, &current, &maximum);
        return new(current, maximum);
    }

    private bool ChangeStamina(GameString* role, int cost, char* reason)
    {
        if (role == null || role->Data == null || role->Length <= 1 || role->Length > role->Capacity)
        { LogFatigue("stamina change rejected: invalid role"); return false; }
        StaminaState before = ReadStamina(role);
        if (!before.IsValid) { LogFatigue("stamina change rejected: invalid current/maximum"); return false; }
        // Bound negative costs before native signed subtraction; even int.MinValue
        // must not overflow. A full refill is a policy, not a special native item.
        int bounded = cost < 0 ? Math.Max(cost, before.Current - before.Maximum) : cost;
        if (bounded == 0) return true;
        var previous = staminaWriter;
        staminaWriter = this;
        try
        {
            ((delegate* unmanaged<GameString*, int, char*, void>)At(SupportedBuild.DeductStamina))(role, bounded, reason);
        }
        finally { staminaWriter = previous; }
        StaminaState after = ReadStamina(role);
        int expected = (int)Math.Clamp((long)before.Current - bounded, 0, before.Maximum);
        if (after.IsValid && after.Current == expected) return true;
        LogFatigue($"stamina readback failed: before={before.Current}, cost={bounded}, after={after.Current}, maximum={after.Maximum}");
        return false;
    }

    private void LogFatigue(string message) => LogGameplay("fatigue", message);
}
