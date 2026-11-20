using System.Runtime.InteropServices;

namespace Dollhouse.Gameplay;

public static unsafe partial class Exports
{
    [UnmanagedCallersOnly(EntryPoint = GameplayLibrary.UseItem)]
    public static void UseItem(nint bag, nint request)
    {
        try { ItemUsePatch.UseItem(Game, bag, request); }
        catch (Exception error) { Report(error); }
    }

    [UnmanagedCallersOnly(EntryPoint = GameplayLibrary.ReflectedUseItem)]
    public static void ReflectedUseItem(nint unused, nint* arguments)
    {
        try { if (arguments != null) ItemUsePatch.UseItem(Game, arguments[0], arguments[1]); }
        catch (Exception error) { Report(error); }
    }

    [UnmanagedCallersOnly(EntryPoint = GameplayLibrary.ItemUseStock)]
    public static long AvailableStock(int itemId)
    {
        try { return ItemUsePatch.AvailableStock(Game, itemId); }
        catch (Exception error) { Report(error); return 0; }
    }

    [UnmanagedCallersOnly(EntryPoint = GameplayLibrary.ItemUseCompleted)]
    public static void Completed(nint bag, nint request, int count)
    {
        try { ItemUsePatch.CompleteSuccessfulUse(Game, bag, request); }
        catch (Exception error) { Report(error); }
    }
}
