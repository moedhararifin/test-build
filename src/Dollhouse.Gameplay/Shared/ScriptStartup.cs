using System.Runtime.InteropServices;

namespace Dollhouse.Gameplay;

public static unsafe partial class Exports
{
    // Keep the published export name. This shared precompiled-loading boundary
    // retains metadata and initializes independent script repairs before cleanup.
    [UnmanagedCallersOnly(EntryPoint = GameplayLibrary.InstallCameraLoader)]
    internal static void InitializeScriptRepairs(nint precompiledData)
    {
        try { Game.InitializeBulgeEligibility(precompiledData); }
        catch (Exception error) { Report(error); }
        try { Game.InstallDrinkEligibility(precompiledData, (nint)(delegate* unmanaged<nint, nint, nint, void>)&DrinkEligibilityEntry); }
        catch (Exception error) { Report(error); }
        try { Game.InstallCameraLoader(precompiledData, (nint)(delegate* unmanaged<nint, nint, nint, void>)&CameraLoadEntry); }
        catch (Exception error) { CameraFailure("installation", error); }
    }
}
