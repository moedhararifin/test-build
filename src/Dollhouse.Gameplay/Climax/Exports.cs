using System.Runtime.InteropServices;

namespace Dollhouse.Gameplay;

public static unsafe partial class Exports
{
    // Native member binding: rcx=proxy (unused), rdx=borrowed FString*, result in xmm0.
    [UnmanagedCallersOnly(EntryPoint = GameplayLibrary.FemaleThreshold)]
    internal static float FemaleEcstasyThreshold(nint proxy, GameString* role)
    {
        try { return Game.FemaleEcstasyThreshold(role); }
        catch (Exception error) { Report(error); return ClimaxPatch.DefaultFemaleThreshold; }
    }

    // The registration accessor writes a function pointer into the engine's supplied storage.
    [UnmanagedCallersOnly(EntryPoint = GameplayLibrary.FemaleThresholdFunction)]
    internal static void FemaleThresholdFunction(nint* destination) =>
        *destination = (nint)(delegate* unmanaged<nint, GameString*, float>)&FemaleEcstasyThreshold;

    // Generic binding: arguments[0]=proxy, arguments[1]=borrowed FString*, r8=result storage.
    [UnmanagedCallersOnly(EntryPoint = GameplayLibrary.GenericFemaleThreshold)]
    internal static void GenericFemaleThreshold(nint unused, nint* arguments, float* result)
    {
        *result = ClimaxPatch.DefaultFemaleThreshold;
        try { *result = Game.FemaleEcstasyThreshold((GameString*)arguments[1]); }
        catch (Exception error) { Report(error); }
    }

    // Called after Unreal parsed an owned FString; the bridge completes the frame and releases it.
    [UnmanagedCallersOnly(EntryPoint = GameplayLibrary.ExecFemaleThreshold)]
    internal static void ExecFemaleThreshold(GameString* role, nint frame, float* result)
    {
        *result = ClimaxPatch.DefaultFemaleThreshold;
        try { Game.CompleteThresholdExec(role, frame, result); }
        catch (Exception error) { Report(error); }
    }
}
