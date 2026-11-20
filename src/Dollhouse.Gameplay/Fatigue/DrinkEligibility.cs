namespace Dollhouse.Gameplay;

internal sealed unsafe partial class NativeGame
{
    private const string DrinkModule = "Core.BP.Interact.BP_Drink";
    private const string DrinkType = "A_BP_Drink";

    private nint DrinkMethod(nint type, ReadOnlySpan<byte> name)
    {
        fixed (byte* text = name)
            return ((delegate* unmanaged<nint, byte*, nint>)At(SupportedBuild.ScriptMethodByName))(type, text);
    }

    internal void InstallDrinkEligibility(nint precompiled, nint replacement)
    {
        if (precompiled == 0 || replacement == 0) { LogFatigue("drink eligibility installation failed: missing startup data"); return; }
        nint engine = *(nint*)(precompiled + SupportedBuild.PrecompiledEngine);
        nint type = engine == 0 ? 0 : FindScriptType(engine, DrinkModule, DrinkType);
        nint function = type == 0 ? 0 : DrinkMethod(type, "GetIsDrunk_Implementation\0"u8);
        if (function == 0) { LogFatigue("drink eligibility installation failed: method unavailable"); return; }
        nint* entry = (nint*)(function + SupportedBuild.ScriptVmEntry);
        if (!IsDrinkEligibilityMethod(function, engine) || (*entry != 0 && *entry != replacement))
        { LogFatigue("drink eligibility installation refused: signature/entries changed"); return; }
        if (DrinkMethod(type, "GetOwnerPlayerData\0"u8) == 0)
        { LogFatigue("drink eligibility installation refused: owner metadata missing"); return; }
        *entry = replacement;
    }

    private bool IsDrinkEligibilityMethod(nint function, nint engine)
    {
        uint flags = 0;
        return ScriptString(((delegate* unmanaged<nint, nint>)At(SupportedBuild.ScriptFunctionObjectName))(function)) == DrinkType &&
            ScriptString(((delegate* unmanaged<nint, nint>)At(SupportedBuild.ScriptFunctionName))(function)) == "GetIsDrunk_Implementation" &&
            *(nint*)(function + SupportedBuild.ScriptFunctionEngine) == engine &&
            ((delegate* unmanaged<nint, uint>)At(SupportedBuild.ScriptParameterCount))(function) == 0 &&
            ((delegate* unmanaged<nint, uint*, int>)At(SupportedBuild.ScriptReturnType))(function, &flags) == 1 && flags == 0 &&
            *(int*)(function + SupportedBuild.ScriptArgumentWords) == 2 &&
            *(int*)(function + SupportedBuild.ScriptFunctionKind) == 1 &&
            *(nint*)(function + SupportedBuild.ScriptParameterEntry) == 0 &&
            *(nint*)(function + SupportedBuild.ScriptRawEntry) == 0;
    }

    internal bool CannotDrink(nint actor)
    {
        if (actor == 0) return true;
        bool blocked = true;
        bool completed = WithScriptContext(actor, (context, engine) =>
        {
            nint type = FindScriptType(engine, DrinkModule, DrinkType);
            if (type == 0 || !IsNativeInstance(actor, NativeClassForScriptType(type))) return false;
            nint owner = DrinkMethod(type, "GetOwnerPlayerData\0"u8);
            if (owner == 0 || ((delegate* unmanaged<nint, uint>)At(SupportedBuild.ScriptParameterCount))(owner) != 0 ||
                !PrepareScriptCall(context, owner, actor, "drink owner") || !ExecuteScriptCall(context, "drink owner")) return false;
            // Preserve the original early rejection when its owner getter returns
            // null. Returned handles belong to this context; never retain/release.
            if (((delegate* unmanaged<nint, nint>)At(SupportedBuild.ScriptReturnObject))(context) == 0) return true;
            if (!FatigueEnabled) { blocked = false; return true; }
            int roleId = *(int*)(actor + SupportedBuild.DrinkOwnerRoleId);
            nint row = ((delegate* unmanaged<int, nint>)At(SupportedBuild.FemaleById))(roleId);
            if (row == 0) return false;
            StaminaState stamina = ReadStamina((GameString*)(row + SupportedBuild.FemaleRowRoleName));
            if (!stamina.IsValid) return false;
            blocked = FatiguePolicy.CannotAfford(true, stamina.Current, DrinkStaminaCost());
            return true;
        });
        if (!completed) LogFatigue("drink eligibility failed: owner, role or stamina unavailable; action refused");
        return blocked;
    }
}
