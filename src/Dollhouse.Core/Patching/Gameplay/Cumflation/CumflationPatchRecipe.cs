using Dollhouse.Core.Patching.Native;
using static Iced.Intel.Register;

namespace Dollhouse.Core.Patching;

internal static class CumflationPatchRecipe
{
    internal static void AddTo(NativePatchPlan patch)
    {
        patch.RedirectCall("Snapshot drain volumes before the first native zero write", SupportedBuild.CaptureDrainWrite,
            SupportedBuild.WriteRoleAttribute, patch.Import(GameplayLibrary.CaptureDrain));
        patch.RedirectCall("Route drain alerts according to character-menu visibility", SupportedBuild.DrainClearedBroadcast,
            SupportedBuild.Broadcast, patch.Import(GameplayLibrary.DispatchDrainCleared));
        var right = patch.Import(GameplayLibrary.ShowDrainRightMsg);
        patch.RedirectCall("Supply the expelled amount to direct right-message dispatch", SupportedBuild.DrainRightDirectCall,
            SupportedBuild.ShowRightMsg, right);
        patch.RedirectCall("Supply the expelled amount to reflected right-message dispatch", SupportedBuild.DrainRightExecCall,
            SupportedBuild.ShowRightMsg, right);
        patch.RedirectCall("Advance experiment belly growth from the native frame loop",
            SupportedBuild.FrameTickerCall, SupportedBuild.FrameTickerTick, patch.Import(GameplayLibrary.TickCumflation));
        // CameraPatchRecipe owns the shared startup hook. It retains the metadata
        // also required by the existing experiment and drain UI calls.
        var capacity = patch.Import(GameplayLibrary.SemenCapacity);
        patch.RedirectToLeaf("Supply character capacity to all role-attribute consumers",
            SupportedBuild.ReadRoleAttribute,
            expect: code => code.StoreOnStack(8, RBX),
            implementation: code =>
            {
                // Replay the overwritten prologue store without changing RSP/nonvolatile registers.
                // Other attributes rejoin the original prologue, keeping its unwind metadata valid.
                code.StoreOnStack(8, RBX);
                code.Compare(EDX, SupportedBuild.SemenCapacityAttribute);
                code.JumpIfNotEqual(SupportedBuild.ReadRoleAttributeResume);
                code.Jump(capacity); // Tail call: retain original FString*, stack and unwind ABI.
            });
        var persist = patch.Import(GameplayLibrary.PersistCumflationWithContext);
        patch.RedirectToLeaf("Retain exec world context during request parsing", SupportedBuild.PropertyExecEntry,
            expect: code => code.StoreOnStack(8, RBX),
            implementation: code =>
            {
                code.StoreOnStack(SupportedBuild.PropertyExecContextHome, RCX);
                code.StoreOnStack(8, RBX);
                code.Jump(SupportedBuild.PropertyExecResume);
            });
        var cleared = patch.Import(GameplayLibrary.NotifyCumflationCleared);
        patch.RedirectCall("Publish cleared volumes to live room state", SupportedBuild.ClearVolumesBroadcast,
            SupportedBuild.Broadcast, cleared);
        patch.ReplaceBlock("Persist volumes in direct role-property dispatch",
            SupportedBuild.PropertyDirectDispatch, SupportedBuild.PropertyDirectDispatchEnd,
            expect: code => code.Call(SupportedBuild.LocalProgressionGate),
            replacement: code =>
            {
                code.Move(R8, RCX); // Direct member-call receiver is still live.
                code.Move(ECX, EDI);
                code.Move(RDX, RBX);
                code.Call(persist);
                code.Jump(SupportedBuild.PropertyDirectCleanup);
            });
        patch.ReplaceBlock("Persist volumes in generic script role-property dispatch",
            SupportedBuild.PropertyGenericDispatch, SupportedBuild.PropertyGenericDispatchEnd,
            expect: code => code.Call(SupportedBuild.LocalProgressionGate),
            replacement: code =>
            {
                code.Load(R8, RDI, 0); // arguments[0] is the proxy/world context.
                code.Move(ECX, EBX);
                code.AddressOnStack(RDX, 0x40);
                code.Call(persist);
                code.Jump(SupportedBuild.PropertyGenericCleanup);
            });
        patch.ReplaceBlock("Persist volumes in Unreal role-property dispatch",
            SupportedBuild.PropertyExecDispatch, SupportedBuild.PropertyExecDispatchEnd,
            expect: code => code.Call(SupportedBuild.LocalProgressionGate),
            replacement: code =>
            {
                code.Load(R8, RBP, SupportedBuild.PropertyExecContextFromFrame);
                code.Move(ECX, EBX);
                code.Address(RDX, RBP, -0x20);
                code.Call(persist);
                code.Jump(SupportedBuild.PropertyExecCleanup);
            });
    }
}
