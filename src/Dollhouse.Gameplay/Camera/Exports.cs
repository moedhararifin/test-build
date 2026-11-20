using System.Runtime.InteropServices;

namespace Dollhouse.Gameplay;

public static unsafe partial class Exports
{
    // Native VM-entry ABI verified at 0x50e4509 and 0x5129229. No global file I/O
    // binding is redirected. Both engine dispatchers retain their own stack cleanup.
    [UnmanagedCallersOnly]
    internal static void CameraLoadEntry(nint execution, nint arguments, nint result)
    {
        try { Game.LoadCameraPositions(arguments); }
        catch (Exception error) { CameraFailure("load", error); }
    }

    private static void CameraFailure(string operation, Exception error)
    {
        try { Game.LogCamera($"{operation} failed: {error.GetType().Name}: {error.Message}"); }
        catch { try { Report(error); } catch { /* Never cross an unmanaged boundary. */ } }
    }
}
