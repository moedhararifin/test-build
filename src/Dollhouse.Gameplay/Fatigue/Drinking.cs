namespace Dollhouse.Gameplay;

internal sealed unsafe partial class NativeGame
{
    private int DrinkStaminaCost() => Math.Max(0,
        ((delegate* unmanaged<char*, int, int>)At(SupportedBuild.ReadIntegerConfig))((char*)At(SupportedBuild.DrinkStaminaCostKey), 3));

    internal void ApplyDrinkStamina(GameString* role)
    {
        if (FatigueEnabled)
            fixed (char* reason = "RoomRoleDrink") ChangeStamina(role, DrinkStaminaCost(), reason);
        // This call belongs to the old gated block, but is NOT stamina spending.
        // Its native mode policy must remain independent of the Fatigue toggle.
        if (((delegate* unmanaged<byte>)At(SupportedBuild.LocalProgressionGate))() != 0)
            ((delegate* unmanaged<void>)At(SupportedBuild.DrinkBookkeeping))();
    }
}
