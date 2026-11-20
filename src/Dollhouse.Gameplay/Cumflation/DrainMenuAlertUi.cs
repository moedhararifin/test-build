namespace Dollhouse.Gameplay;

internal sealed unsafe partial class NativeGame
{
    private struct DrainWidgets { internal nint* Data; internal int Count, Capacity; }

    private bool IsDrainMenuOpen(nint engine, nint world)
    {
        nint type = FindScriptType(engine, "Core.GUI.Character.UICharacter", "U_UICharacter");
        if (type == 0) return false;
        nint nativeClass = NativeClassForScriptType(type);
        if (nativeClass == 0) return false;
        DrainWidgets widgets = default;
        try
        {
            // One event-scoped engine query, restricted to this world's menu class.
            // Child OnOpen subscriptions can outlive the parent menu's visible session.
            ((delegate* unmanaged<nint, DrainWidgets*, nint*, byte, void>)At(SupportedBuild.AllWidgetsOfClass))
                (world, &widgets, &nativeClass, 0);
            if (widgets.Count < 0 || widgets.Count > widgets.Capacity || (widgets.Count > 0 && widgets.Data == null))
            { LogCumflation("drain menu lookup rejected: invalid widget array"); return false; }
            bool open = false;
            for (int i = 0; i < widgets.Count; i++)
            {
                nint menu = widgets.Data[i];
                if (!IsNativeInstance(menu, nativeClass)) continue;
                bool candidate = ((delegate* unmanaged<nint, byte>)At(SupportedBuild.WidgetVisible))(menu) != 0;
                open |= candidate;
            }
            return open;
        }
        finally { FreeAllocation(widgets.Data); }
    }

    private bool ShowDrainMenuAlert(nint context, nint engine, nint uiType, nint widget, DrainNotice notice)
    {
        string template = DrainLanguage(context, engine, "RoleReleseLiquid");
        string name = DrainLanguage(context, engine, notice.Role);
        string? text = notice.Format(template, name);
        if (text == null || name.Length == 0)
        { LogCumflation("drain alert localization unavailable or unsupported template"); return false; }
        nint show = DrainUiMethod(uiType, "ShowDrainFluidsTip", 0);
        if (show == 0) return false;
        // Use the panel's original helper, preserving its placement, sound, replacement
        // behavior and eight-second dismissal timer. Correct its text synchronously,
        // before the newly created widget can render the cleared-field zero amount.
        if (!PrepareScriptCall(context, show, widget, "ShowDrainFluidsTip") ||
            !ExecuteScriptCall(context, "ShowDrainFluidsTip")) return false;
        if (!TryScriptMember(uiType, "ShowDrainFluidsTip_TopTips", 8, out var member)) return false;
        nint field = widget + member.Offset;
        int id = member.TypeId;
        nint type = ((delegate* unmanaged<nint, int, nint>)At(SupportedBuild.ScriptTypeById))(engine, id);
        if (type == 0 || ScriptString(((delegate* unmanaged<nint, nint>)At(SupportedBuild.ScriptTypeName))(type)) != "U_UI_TopTips")
        { LogCumflation("drain menu tip rejected: unexpected property type"); return false; }
        nint tip = *(nint*)field;
        if (!IsNativeInstance(tip, NativeClassForScriptType(type)))
        { LogCumflation("drain menu tip rejected: widget absent or wrong class"); return false; }
        nint set;
        fixed (byte* method = "SetText\0"u8)
            set = ((delegate* unmanaged<nint, byte*, nint>)At(SupportedBuild.ScriptMethodByName))(type, method);
        if (set == 0) { LogCumflation("drain menu tip SetText absent"); return false; }
        string owner = ScriptString(((delegate* unmanaged<nint, nint>)At(SupportedBuild.ScriptFunctionObjectName))(set));
        uint parameters = ((delegate* unmanaged<nint, uint>)At(SupportedBuild.ScriptParameterCount))(set);
        int result = ((delegate* unmanaged<nint, uint*, int>)At(SupportedBuild.ScriptReturnType))(set, null);
        bool valid = owner == "U_UI_TopTips" && parameters == 2 && result == 0;
        if (!valid) LogCumflation($"drain tip signature rejected: owner={owner}, parameters={parameters}, returnType={result:x}");
        for (uint i = 0; i < parameters; i++)
        {
            int argument = 0; uint modifiers = 0;
            int status = ((delegate* unmanaged<nint, uint, int*, uint*, nint*, nint*, int>)At(SupportedBuild.ScriptParameter))
                (set, i, &argument, &modifiers, null, null);
            nint argumentType = i == 0 ? ((delegate* unmanaged<nint, int, nint>)At(SupportedBuild.ScriptTypeById))(engine, argument) : 0;
            bool accepted = status == 0 && (i == 0
                ? argumentType != 0 && ScriptString(((delegate* unmanaged<nint, nint>)At(SupportedBuild.ScriptTypeName))(argumentType)) == "FString" && (modifiers == 5 || modifiers == 7)
                : i == 1 && argument == 1 && (modifiers == 0 || modifiers == 4));
            if (!accepted) LogCumflation($"drain tip parameter rejected: index={i}, status={status}, type={argument:x}, modifiers={modifiers:x}");
            valid &= accepted;
        }
        if (!valid) return false;
        if (!PrepareScriptCall(context, set, tip, "drain tip SetText")) return false;
        fixed (char* chars = text)
        {
            GameString content = new() { Data = chars, Length = text.Length + 1, Capacity = text.Length + 1 };
            int argument = ((delegate* unmanaged<nint, uint, nint, int>)At(SupportedBuild.ScriptSetArgAddress))(context, 0, (nint)(&content));
            int translate = ((delegate* unmanaged<nint, uint, byte, int>)At(SupportedBuild.ScriptSetArgByte))(context, 1, 0);
            if (argument < 0 || translate < 0)
            { LogCumflation($"drain tip arguments rejected: text={argument}, translate={translate}"); return false; }
            return ExecuteScriptCall(context, "drain tip SetText");
        }
    }
}
