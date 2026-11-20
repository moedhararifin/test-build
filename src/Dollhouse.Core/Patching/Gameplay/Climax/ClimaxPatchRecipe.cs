using Dollhouse.Core.Patching.Native;
using static Iced.Intel.Register;

namespace Dollhouse.Core.Patching;

internal static class ClimaxPatchRecipe
{

    internal static void AddTo(NativePatchPlan patch)
    {
        var accessor = patch.Import(GameplayLibrary.FemaleThresholdFunction);
        var generic = patch.Import(GameplayLibrary.GenericFemaleThreshold);
        var exec = patch.Import(GameplayLibrary.ExecFemaleThreshold);

        // Redirect this method's bindings, never the shared return-100 leaf used elsewhere.
        patch.RedirectFunction("Supply the relationship-aware threshold function",
            SupportedBuild.FemaleThresholdAccessor, accessor,
            expect: code => code.LoadAddress(RAX, SupportedBuild.Constant100Function));
        patch.RedirectFunction("Calculate the threshold through the generic script binding",
            SupportedBuild.FemaleThresholdGeneric, generic,
            expect: code => code.Store(R8, BitConverter.SingleToUInt32Bits(100f)));

        // Retain Unreal's original FString argument parsing and epilogue/unwind layout.
        // At this point: frame=rbx, result=rsi, owned RoleName=[rsp+20h].
        patch.ReplaceBlock("Finish the reflected threshold call using the same relationship rule",
            SupportedBuild.FemaleThresholdExecTail, SupportedBuild.FemaleThresholdExecCleanupEnd,
            expect: code => code.Load(RAX, RBX, SupportedBuild.ScriptFrameCode),
            replacement: code =>
            {
                code.AddressOnStack(RCX, 0x20);
                code.Move(RDX, RBX);
                code.Move(R8, RSI);
                code.Call(exec);
            });
    }
}
