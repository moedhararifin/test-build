using System.Text;

namespace Dollhouse.Gameplay;

internal sealed unsafe partial class NativeGame
{
    private string DrainLanguage(nint context, nint engine, string key)
    {
        nint module;
        fixed (byte* name = "BP.GlobalFunctionLibrary\0"u8)
            module = ((delegate* unmanaged<nint, byte*, int, nint>)At(SupportedBuild.ScriptGetModule))(engine, name, 0);
        if (module == 0 || ((delegate* unmanaged<nint, nint>)At(SupportedBuild.ScriptModuleEngine))(module) != engine)
        { LogCumflation("drain localization unavailable: BP.GlobalFunctionLibrary module missing"); return ""; }
        uint count = ((delegate* unmanaged<nint, uint>)At(SupportedBuild.ScriptModuleFunctionCount))(module);
        nint function = 0;
        for (uint i = 0; i < count; i++)
        {
            nint candidate = ((delegate* unmanaged<nint, uint, nint>)At(SupportedBuild.ScriptModuleFunction))(module, i);
            if (candidate == 0 || ScriptString(((delegate* unmanaged<nint, nint>)At(SupportedBuild.ScriptFunctionName))(candidate)) != "GetLanguage") continue;
            string space = ScriptString(((delegate* unmanaged<nint, nint>)At(SupportedBuild.ScriptFunctionNamespace))(candidate));
            int id = ((delegate* unmanaged<nint, uint*, int>)At(SupportedBuild.ScriptReturnType))(candidate, null);
            nint type = ((delegate* unmanaged<nint, int, nint>)At(SupportedBuild.ScriptTypeById))(engine, id);
            uint parameters = ((delegate* unmanaged<nint, uint>)At(SupportedBuild.ScriptParameterCount))(candidate);
            string resultType = type == 0 ? "<missing>" : ScriptString(((delegate* unmanaged<nint, nint>)At(SupportedBuild.ScriptTypeName))(type));
            int argumentId = 0;
            uint modifiers = 0;
            int argumentStatus = parameters == 1 ? ((delegate* unmanaged<nint, uint, int*, uint*, nint*, nint*, int>)At(SupportedBuild.ScriptParameter))
                (candidate, 0, &argumentId, &modifiers, null, null) : -1;
            bool valid = parameters == 1 && resultType == "FString" && space == "GlobalFunctionLibrary" &&
                argumentStatus == 0 && (argumentId & 0x1fffffff) == (id & 0x1fffffff) &&
                // This build reports const FString& as CONST|INOUTREF (7) in live
                // metadata. Both const input-reference forms borrow the same address;
                // reject mutable/output-only parameters before lending pinned storage.
                (modifiers == 5 || modifiers == 7);
            if (!valid)
            { LogCumflation($"localization signature rejected: namespace={space}, parameters={parameters}, returnType={resultType}, argumentType={argumentId:x}, modifiers={modifiers:x}"); continue; }
            if (function != 0) { LogCumflation("drain localization rejected: ambiguous GetLanguage"); return ""; }
            function = candidate;
        }
        if (function == 0) { LogCumflation("localization GetLanguage unavailable"); return ""; }
        if (!PrepareScriptCall(context, function, 0, "GetLanguage")) return "";
        fixed (char* chars = key)
        {
            GameString argument = new() { Data = chars, Length = key.Length + 1, Capacity = key.Length + 1 };
            int assigned = ((delegate* unmanaged<nint, uint, nint, int>)At(SupportedBuild.ScriptSetArgAddress))(context, 0, (nint)(&argument));
            if (assigned < 0) { ScriptCallFailed("GetLanguage argument", assigned); return ""; }
            if (!ExecuteScriptCall(context, "GetLanguage")) return "";
            var result = (GameString*)((delegate* unmanaged<nint, nint>)At(SupportedBuild.ScriptReturnObject))(context);
            return ReadDrainString(result); // Copy before the next Prepare returns context-owned storage.
        }
    }

    private nint DrainUiMethod(nint type, string name, uint parameters)
    {
        nint function;
        fixed (byte* text = Encoding.UTF8.GetBytes(name + '\0'))
            function = ((delegate* unmanaged<nint, byte*, nint>)At(SupportedBuild.ScriptMethodByName))(type, text);
        if (function == 0)
        { LogCumflation($"drain UI method absent: {name}"); return 0; }
        string actualOwner = ScriptString(((delegate* unmanaged<nint, nint>)At(SupportedBuild.ScriptFunctionObjectName))(function));
        uint count = ((delegate* unmanaged<nint, uint>)At(SupportedBuild.ScriptParameterCount))(function);
        uint returnModifiers = 0;
        int result = ((delegate* unmanaged<nint, uint*, int>)At(SupportedBuild.ScriptReturnType))(function, &returnModifiers);
        bool valid = actualOwner == "U_UICharacterState" && count == parameters && result == 0 && returnModifiers == 0;
        for (uint i = 0; i < count; i++)
        {
            int id = 0;
            uint modifiers = 0;
            int status = ((delegate* unmanaged<nint, uint, int*, uint*, nint*, nint*, int>)At(SupportedBuild.ScriptParameter))
                (function, i, &id, &modifiers, null, null);
            int expected = name == "DelaySetFluidProgress" ? 11 : 1;
            // Saved signatures use const double/bool by value (GetParam CONST=4).
            // Const does not change SetArgDouble/Byte ABI. References still require
            // an address and must never pass this value-argument validation.
            bool accepted = status == 0 && id == expected && (modifiers == 0 || modifiers == 4);
            if (!accepted) LogCumflation($"drain UI parameter rejected: method={name}, index={i}, status={status}, type={id:x}, expectedType={expected:x}, modifiers={modifiers:x}");
            valid &= accepted;
        }
        if (!valid) LogCumflation($"drain UI signature rejected: method={name}, owner={actualOwner}, parameters={count}, returnType={result:x}, returnModifiers={returnModifiers:x}");
        return valid ? function : 0;
    }

    private bool DrainUiReset(nint context, nint widget, nint clear, nint progress, nint button)
    {
        if (!PrepareScriptCall(context, clear, widget, "clear displayed volumes") ||
            !ExecuteScriptCall(context, "clear displayed volumes")) return false;
        if (!PrepareScriptCall(context, progress, widget, "animate zero progress")) return false;
        var setDouble = (delegate* unmanaged<nint, uint, double, int>)At(SupportedBuild.ScriptSetArgDouble);
        int target = setDouble(context, 0, 0), time = setDouble(context, 1, .5), delta = setDouble(context, 2, .01);
        if (target < 0 || time < 0 || delta < 0)
        { LogCumflation($"drain progress arguments rejected: target={target}, time={time}, delta={delta}"); return false; }
        if (!ExecuteScriptCall(context, "animate zero progress") ||
            !PrepareScriptCall(context, button, widget, "disable drain button")) return false;
        int enabled = ((delegate* unmanaged<nint, uint, byte, int>)At(SupportedBuild.ScriptSetArgByte))(context, 0, 0);
        if (enabled < 0) return ScriptCallFailed("drain button argument", enabled);
        return ExecuteScriptCall(context, "disable drain button");
    }
}
