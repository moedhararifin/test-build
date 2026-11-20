using Dollhouse.Core.Patching.Native;
using static Iced.Intel.Register;

namespace Dollhouse.Core.Patching;

internal static class FatiguePatchRecipe
{
    internal static void AddTo(NativePatchPlan patch)
    {
        // Same seven-byte span and native flags contract. No callback or second
        // stamina lookup: use the exhaustion result the game just calculated.
        patch.ReplaceBlock("Connect native card exhaustion to its click lock", SupportedBuild.CardStaminaLock, SupportedBuild.CardStaminaLock + 7,
            code => code.CompareByte(RSI, CardFilteringBindings.PoseLocked, R15L),
            code => code.OrByte(RSI, CardFilteringBindings.PoseLocked, BPL));
        // These wrappers retain their native prologues/epilogues and unwind data.
        // Skip only the fabricated full-stamina branches, not the global mode.
        foreach (var (site, target) in new[]
        {
            (SupportedBuild.StaminaReaderMode, SupportedBuild.StaminaReaderReal),
            (SupportedBuild.StaminaSummaryMode, SupportedBuild.StaminaSummaryReal)
        })
            patch.ReplaceBlock("Read real character stamina", site, site + 9,
                code => code.Call(SupportedBuild.ProfileMode), code => code.Jump(target));
        patch.RedirectCall("Authorize only scoped stamina progression saves", SupportedBuild.StaminaSaveMode,
            SupportedBuild.ProfileMode, patch.Import(GameplayLibrary.StaminaProgressionMode));
        var drink = patch.Import(GameplayLibrary.DrinkStamina);
        patch.ReplaceBlock("Apply drink stamina after native effect acceptance", SupportedBuild.DrinkEffectCost, SupportedBuild.DrinkEffectCostEnd,
            code => code.Call(SupportedBuild.LocalProgressionGate), code =>
            {
                code.Move(RCX, RSI);
                code.Call(drink);
            });
        patch.ReplaceBlock("Apply drink stamina in local request completion", SupportedBuild.DrinkRequestCost, SupportedBuild.DrinkRequestCostEnd,
            code => code.Call(SupportedBuild.LocalProgressionGate), code =>
            {
                code.Address(RCX, RBP, SupportedBuild.DrinkRequestRoleFromFrame);
                code.Call(drink);
            });
    }
}
