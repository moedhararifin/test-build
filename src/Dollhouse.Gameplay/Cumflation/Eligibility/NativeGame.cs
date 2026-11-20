namespace Dollhouse.Gameplay;

internal sealed unsafe partial class NativeGame
{
    private nint eligibilityEngine;
    private int eligibilityFunctionId;
    private bool eligibilityFailureLogged;

    internal void DispatchBulgeEligibility(nint context, nint function)
    {
        bool handled = false;
        try
        {
            // Engine + runtime ID identify the global validated at startup. Unrelated
            // script calls do no metadata work, allocate nothing and remain silent.
            handled = IgnoreCumflationRestrictions && eligibilityEngine != 0 && context != 0 && function != 0 &&
                *(nint*)(context + SupportedBuild.ScriptContextEngine) == eligibilityEngine &&
                *(int*)(function + SupportedBuild.ScriptFunctionId) == eligibilityFunctionId &&
                TryAllowExperimentBulge(context, function);
        }
        catch (Exception error) { RejectBulgeEligibility($"dispatch failed: {error.GetType().Name}"); }
        if (!handled)
            ((delegate* unmanaged<nint, nint, void>)At(SupportedBuild.ScriptDispatch))(context, function);
    }

    private bool TryAllowExperimentBulge(nint context, nint function)
    {
        int state = *(int*)(context + SupportedBuild.ScriptContextState);
        int words = *(int*)(function + SupportedBuild.ScriptArgumentWords);
        int kind = *(int*)(function + SupportedBuild.ScriptFunctionKind);
        nint stack = *(nint*)(context + SupportedBuild.ScriptStackPointer);
        if (*(nint*)(function + SupportedBuild.ScriptFunctionEngine) != eligibilityEngine || HasNativeScriptEntry(function))
            return RejectBulgeEligibility("helper engine or native execution entries changed");
        if (state != 6 || words != 1 || kind != 1 || stack == 0 || (stack & 3) != 0 || stack > nint.MaxValue - 4)
            return RejectBulgeEligibility($"frame state={state}, argumentWords={words}, functionKind={kind}, stackAvailable={stack != 0}");

        // Same scalar-return/argument-pop contract as the native dispatcher. No
        // helper frame was entered, so it created no locals or handles to destroy.
        *(ulong*)(context + SupportedBuild.ScriptValueRegister) = 1;
        *(nint*)(context + SupportedBuild.ScriptStackPointer) = stack + 4;
        return true;
    }

    private bool RejectBulgeEligibility(string reason)
    {
        if (!eligibilityFailureLogged)
        {
            eligibilityFailureLogged = true;
            LogCumflation($"eligibility override refused: {reason}; original dispatch preserved");
        }
        return false;
    }

    private static bool HasNativeScriptEntry(nint function) =>
        *(nint*)(function + SupportedBuild.ScriptVmEntry) != 0 ||
        *(nint*)(function + SupportedBuild.ScriptParameterEntry) != 0 ||
        *(nint*)(function + SupportedBuild.ScriptRawEntry) != 0;
}
