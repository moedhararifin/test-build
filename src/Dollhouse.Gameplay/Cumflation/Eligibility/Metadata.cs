namespace Dollhouse.Gameplay;

internal sealed unsafe partial class NativeGame
{
    internal void InitializeBulgeEligibility(nint precompiledData)
    {
        eligibilityEngine = 0;
        eligibilityFunctionId = 0;
        eligibilityFailureLogged = false;
        try
        {
            if (precompiledData == 0) { RejectBulgeEligibility("precompiled data unavailable"); return; }
            nint engine = *(nint*)(precompiledData + SupportedBuild.PrecompiledEngine);
            if (engine == 0) { RejectBulgeEligibility("script engine unavailable"); return; }
            nint module = FindEligibilityModule(engine);
            if (module == 0) return;
            nint function = FindEligibilityFunction(module);
            if (function == 0 || !ValidateBulgeEligibility(engine, module, function)) return;
            int id = *(int*)(function + SupportedBuild.ScriptFunctionId);
            if (id <= 0) { RejectBulgeEligibility("invalid runtime function id"); return; }
            // This fully-precompiled build has no hot reload. Keep engine identity
            // and its integer function ID, never a borrowed function/type/UObject.
            eligibilityEngine = engine;
            eligibilityFunctionId = id;
        }
        catch (Exception error) { RejectBulgeEligibility($"initialization failed: {error.GetType().Name}"); }
    }

    private nint FindEligibilityModule(nint engine)
    {
        nint module;
        fixed (byte* name = "Multiplayer.BP.HM_HGroup\0"u8)
            module = ((delegate* unmanaged<nint, byte*, int, nint>)At(SupportedBuild.ScriptGetModule))(engine, name, 0);
        if (module != 0 && ((delegate* unmanaged<nint, nint>)At(SupportedBuild.ScriptModuleEngine))(module) == engine)
            return module;
        RejectBulgeEligibility("helper module unavailable or belongs to another engine");
        return 0;
    }

    private nint FindEligibilityFunction(nint module)
    {
        nint previous = ((delegate* unmanaged<nint, nint>)At(SupportedBuild.ScriptModuleGetNamespace))(module);
        if (previous == 0) { RejectBulgeEligibility("module namespace unavailable"); return 0; }
        nint function = 0;
        bool restored;
        try
        {
            fixed (byte* space = "HM_HGroup\0"u8)
                if (((delegate* unmanaged<nint, byte*, int>)At(SupportedBuild.ScriptModuleSetNamespace))(module, space) < 0)
                { RejectBulgeEligibility("namespace selection failed"); return 0; }
            fixed (byte* name = "CheckHCardAvailableForBulge\0"u8)
                function = ((delegate* unmanaged<nint, byte*, nint>)At(SupportedBuild.ScriptModuleFunctionByName))(module, name);
        }
        finally
        {
            restored = ((delegate* unmanaged<nint, nint, int>)At(SupportedBuild.ScriptModuleSetNamespace))(module, previous) >= 0;
            if (!restored) RejectBulgeEligibility("namespace restoration failed");
        }
        if (function == 0) RejectBulgeEligibility("eligibility helper unavailable");
        return restored ? function : 0;
    }

    private bool ValidateBulgeEligibility(nint engine, nint module, nint function)
    {
        string name = ScriptString(((delegate* unmanaged<nint, nint>)At(SupportedBuild.ScriptFunctionName))(function));
        string space = ScriptString(((delegate* unmanaged<nint, nint>)At(SupportedBuild.ScriptFunctionNamespace))(function));
        string owner = ScriptString(((delegate* unmanaged<nint, nint>)At(SupportedBuild.ScriptFunctionObjectName))(function));
        if (name != "CheckHCardAvailableForBulge" || space != "HM_HGroup" || owner.Length != 0 ||
            *(nint*)(function + SupportedBuild.ScriptFunctionEngine) != engine ||
            *(nint*)(function + SupportedBuild.ScriptFunctionModule) != module)
            return RejectBulgeEligibility("unexpected helper identity, owner or module");
        uint count = ((delegate* unmanaged<nint, uint>)At(SupportedBuild.ScriptParameterCount))(function);
        uint returnFlags = 0;
        int result = ((delegate* unmanaged<nint, uint*, int>)At(SupportedBuild.ScriptReturnType))(function, &returnFlags);
        if (count != 1 || result != 1 || returnFlags != 0)
            return RejectBulgeEligibility($"signature parameters={count}, returnType={result:x}, returnFlags={returnFlags:x}");
        int argument = 0; uint flags = 0;
        int status = ((delegate* unmanaged<nint, uint, int*, uint*, nint*, nint*, int>)At(SupportedBuild.ScriptParameter))
            (function, 0, &argument, &flags, null, null);
        if (status != 0 || argument != 4 || flags is not (0 or 4))
            return RejectBulgeEligibility($"argument status={status}, type={argument:x}, flags={flags:x}");
        int words = *(int*)(function + SupportedBuild.ScriptArgumentWords);
        int kind = *(int*)(function + SupportedBuild.ScriptFunctionKind);
        if (words != 1 || kind != 1) return RejectBulgeEligibility($"argumentWords={words}, functionKind={kind}");

        // A native entry can bypass the interpreter. Validate both the helper and
        // UpdateBulge at startup instead of repeating metadata lookup on each call.
        if (HasNativeScriptEntry(function)) return RejectBulgeEligibility("eligibility helper has native execution entries");
        nint type = FindScriptType(engine, "Core.BP.H.Common.HPerformer", "A_HPerformer");
        nint update = type == 0 ? 0 : FindUpdateBulge(type);
        if (update == 0 || HasNativeScriptEntry(update))
            return RejectBulgeEligibility("UpdateBulge missing or has native execution entries");
        return true;
    }
}
