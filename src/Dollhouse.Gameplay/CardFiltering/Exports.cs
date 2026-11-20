using System.Runtime.InteropServices;

namespace Dollhouse.Gameplay;

public static unsafe partial class Exports
{
    [UnmanagedCallersOnly(EntryPoint = GameplayLibrary.MatchesCardFilters)]
    internal static byte MatchesCardFilters(nint selector, nint card, nint nativeContext)
    {
        try { return Game.MatchesCardFilters(selector, card, nativeContext); }
        catch (Exception error)
        {
            CardFilteringFailure("matching", error);
            return 0; // Invalid metadata must not broaden the result set.
        }
    }

    [UnmanagedCallersOnly(EntryPoint = GameplayLibrary.SetCardLock)]
    internal static int SetCardLock(nint pose, byte unowned, byte experience)
    {
        try { return Game.SetCardLock(pose, unowned, experience); }
        catch (Exception error)
        {
            CardFilteringFailure("area lock", error);
            return 0;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = GameplayLibrary.RefreshCardFilters)]
    internal static void RefreshCardFilters(nint selector, CardFilterArray<nint>* matchingCards)
    {
        try { Game.RefreshCardFilters(selector, matchingCards); }
        catch (Exception error) { CardFilteringFailure("refresh", error); }
    }

    private static void CardFilteringFailure(string operation, Exception error)
    {
        try { Game.LogCardFiltering(operation, error); }
        catch { /* Lazy NativeGame initialization can also fail at this boundary. */ }
    }
}
