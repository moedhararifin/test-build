using Dollhouse.Core.Patching.Native;

namespace Dollhouse.Core.Patching;

/// <summary>The complete gameplay repair set, installed unless this launch disables gameplay patches.</summary>
internal static class GameplayRuntimePatch
{
    internal static readonly NativePatchPlan Plan = Create();

    private static NativePatchPlan Create()
    {
        var plan = new NativePatchPlan(GameBuild.ExpectedSize);
        ItemUsePatchRecipe.AddTo(plan);
        ClimaxPatchRecipe.AddTo(plan);
        CumflationPatchRecipe.AddTo(plan);
        FatiguePatchRecipe.AddTo(plan);
        CameraPatchRecipe.AddTo(plan);
        SettingsMenuPatchRecipe.AddTo(plan);
        BulgeEligibilityPatchRecipe.AddTo(plan);
        CardFilteringPatchRecipe.AddTo(plan);
        return plan;
    }
}
