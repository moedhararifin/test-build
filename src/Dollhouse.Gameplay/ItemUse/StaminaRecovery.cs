namespace Dollhouse.Gameplay;

internal sealed unsafe partial class NativeGame
{
    public bool RecoverStamina(nint request)
    {
        try
        {
            if (request == 0 || !HasRole(request))
            { ItemUseFailed("Repair Gel stamina recovery rejected: invalid request/role"); return false; }
            var role = &((ItemRequest*)request)->Role;
            StaminaState before = ReadStamina(role);
            if (!before.IsValid)
            { ItemUseFailed("Repair Gel stamina recovery rejected: invalid current/maximum"); return false; }
            // Explicit Dollhouse policy: one gel fills the character's native
            // maximum. Recovery remains available when fatigue spending is OFF.
            bool applied;
            fixed (char* reason = "RepairGel")
                applied = ChangeStamina(role, before.Current - before.Maximum, reason);
            if (applied)
            {
                StaminaState after = ReadStamina(role);
                if (after.IsValid && after.Current == after.Maximum) return true;
            }
            ItemUseFailed("Repair Gel stamina recovery failed; affinity/UI success skipped");
        }
        catch (Exception error)
        { ItemUseFailed($"Repair Gel stamina recovery failed: {error.GetType().Name}; affinity/UI success skipped"); }
        return false;
    }
}
