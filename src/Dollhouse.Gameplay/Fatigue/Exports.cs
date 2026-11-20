using System.Runtime.InteropServices;

namespace Dollhouse.Gameplay;

public static unsafe partial class Exports
{
    // Method VM ABI: arguments[0] is the borrowed UObject receiver. Boolean
    // return uses the native 64-bit scalar register, not an Unreal exec frame.
    [UnmanagedCallersOnly]
    private static void DrinkEligibilityEntry(nint execution, nint arguments, nint result)
    {
        if (result == 0) return;
        *(ulong*)result = 1;
        try { if (arguments != 0) *(ulong*)result = Game.CannotDrink(*(nint*)arguments) ? 1UL : 0UL; }
        catch (Exception error) { Report(error); }
    }
    [UnmanagedCallersOnly(EntryPoint = GameplayLibrary.DrinkStamina)]
    internal static void DrinkStamina(GameString* role)
    { try { Game.ApplyDrinkStamina(role); } catch (Exception error) { Report(error); } }

    [UnmanagedCallersOnly(EntryPoint = GameplayLibrary.StaminaProgressionMode)]
    internal static byte StaminaProgressionMode()
    { try { return Game.StaminaProgressionMode(); } catch (Exception error) { Report(error); return 0; } }
}
