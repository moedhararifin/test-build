using Dollhouse.Core.Patching.Native;
using static Iced.Intel.Register;

namespace Dollhouse.Core.Patching;

/// <summary>Extend the native Game tab with Dollhouse gameplay toggles.</summary>
internal static class SettingsMenuPatchRecipe
{
    internal static void AddTo(NativePatchPlan patch)
    {
        var rows = patch.ReserveData("Expanded native Game-tab rows", SettingsBindings.RowSize * (SettingsBindings.OriginalRowCount + SettingsBindings.AddedRowCount));
        var build = patch.Import(SettingsBindings.BuildExport);
        var buildAdapter = patch.Leaf("Supply owned descriptor storage to settings initialization", code =>
        {
            code.LoadAddress(RDX, rows);
            code.Jump(build); // Tail call: preserve the original caller's stack, return address and unwind metadata.
        });
        patch.ReplaceBlock("Initialize and build the extended settings menu", SettingsBindings.BuildCall, SettingsBindings.BuildCall + 5,
            code => code.Call(SettingsBindings.Build), code => code.Call(buildAdapter));
        // Each original RIP-relative LEA names the same descriptor table. Redirect all readers,
        // including Apply/Reset/Refresh and OnSelect, so every added receiver is valid.
        foreach (var (site, register) in new[]
        {
            (0x5537d87, RCX), (0x5537f28, RAX), (0x5537f3e, RDX), (0x5537f87, RCX),
            (0x5538969, RCX), (0x5538a0d, RCX), (0x553a2a3, RDX), (0x553a5e6, RDX),
            (0x553a7b0, R15), (0x553b4c4, R12), (0x553b6b9, RAX), (0x553bab4, R13)
        })
            patch.ReplaceBlock("Resolve extended settings descriptors", site, site + 7,
                code => code.LoadAddress(register, SettingsBindings.NativeRows), code => code.LoadAddress(register, rows));
        patch.ReplaceBlock("Include the cumflation options", SettingsBindings.RowCountCheck, SettingsBindings.RowCountCheck + 3,
            code => code.Compare(ECX, SettingsBindings.OriginalRowCount), code => code.Compare(ECX, SettingsBindings.OriginalRowCount + SettingsBindings.AddedRowCount));
        var read = patch.Import(SettingsBindings.ReadRowExport);
        patch.RedirectCall("Read new Game-tab selection on construction", SettingsBindings.BuildReadCall, SettingsBindings.ReadRow, read);
        patch.RedirectCall("Read new Game-tab selection on refresh", SettingsBindings.RefreshReadCall, SettingsBindings.ReadRow, read);
        patch.RedirectCall("Stage default cumflation value on Game-tab Reset", SettingsBindings.ResetDefaultCall,
            SettingsBindings.Default, patch.Import(SettingsBindings.DefaultExport));
        patch.RedirectCall("Persist the new setting on Apply", SettingsBindings.ApplySetCall,
            SettingsBindings.Set, patch.Import(SettingsBindings.SetExport));
        patch.RedirectCall("Compare the committed new setting on Apply", SettingsBindings.ApplyEffectiveCall,
            SettingsBindings.Effective, patch.Import(SettingsBindings.EffectiveExport));
        var apply = patch.Import(SettingsBindings.ApplyExport);
        var applyAdapter = patch.Leaf("Forward completed settings Apply exec", code => code.Jump(apply));
        patch.ReplaceBlock("Refresh committed rows after settings Apply", SettingsBindings.ApplyExecTail, SettingsBindings.ApplyExecTail + 5,
            code => code.Jump(SettingsBindings.Apply), code => code.Jump(applyAdapter));
    }
}
