using Dollhouse.Core.Patching.Native;

namespace Dollhouse.Core.Patching;

internal static class BulgeEligibilityPatchRecipe
{
    internal static void AddTo(NativePatchPlan patch)
    {
        var dispatch = patch.Import(GameplayLibrary.DispatchBulgeEligibility);
        patch.RedirectCall("Guard the optional experiment belly eligibility override", SupportedBuild.ScriptCallDispatch,
            SupportedBuild.ScriptDispatch, dispatch);
        patch.RedirectCall("Guard reflected script belly eligibility calls", SupportedBuild.ScriptInterfaceDispatch,
            SupportedBuild.ScriptDispatch, dispatch);
    }
}
