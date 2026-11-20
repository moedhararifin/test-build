using Dollhouse.Core.Patching.Native;
using static Iced.Intel.Register;

namespace Dollhouse.Core.Patching;

/// <summary>Scoped item-use repairs for this game build. Behavior lives in Dollhouse.Gameplay.</summary>
internal static class ItemUsePatchRecipe
{
    internal static void AddTo(NativePatchPlan patch)
    {
        var useItem = patch.Import(GameplayLibrary.UseItem);
        var reflectedUseItem = patch.Import(GameplayLibrary.ReflectedUseItem);
        var stock = patch.Import(GameplayLibrary.ItemUseStock);
        var completed = patch.Import(GameplayLibrary.ItemUseCompleted);

        // Bypass the mode gate at these two entry points, leaving the shared predicate alone.
        patch.RedirectFunction("Request item use", SupportedBuild.RequestUseItem, useItem,
            expect: ItemUsePrologue);
        patch.RedirectFunction("Request item use through reflection", SupportedBuild.ReflectedUseItem, reflectedUseItem,
            expect: ItemUsePrologue);

        // Substitute stock only at the local handler's calls, never at the inventory getter itself.
        patch.RedirectCall("Check available stock", SupportedBuild.StockCheckCall, SupportedBuild.InventoryCount, stock);
        patch.RedirectCall("Report insufficient stock", SupportedBuild.StockDiagnosticCall, SupportedBuild.InventoryCount, stock);

        // This block is reached after successful ordinary-item consumption. Other paths skip it.
        patch.ReplaceBlock("Complete the item effect instead of logging an unimplemented effect",
            start: SupportedBuild.SuccessfulOrdinaryItem,
            end: SupportedBuild.SuccessfulOrdinaryItemEnd,
            expect: code => code.CompareByteAt(SupportedBuild.ItemUseLogVerbosity, 4),
            replacement: code =>
            {
                // At this site: bag=r15, request=r14, count=ebx. Windows x64 arguments use rcx/rdx/r8.
                code.Move(RCX, R15);
                code.Move(RDX, R14);
                code.Move(R8D, EBX);
                code.Call(completed);
            });
    }

    private static void ItemUsePrologue(X64Code code)
    {
        code.StoreOnStack(8, RBX);
        code.Push(RDI);
    }
}
