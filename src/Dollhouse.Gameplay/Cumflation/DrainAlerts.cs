using System.Runtime.InteropServices;

namespace Dollhouse.Gameplay;

internal sealed unsafe partial class NativeGame
{
    private DrainNotice? drainNotice;
    private bool drainBoxed;
    private readonly Stack<(DrainNotice? Notice, bool Boxed)> previousDrains = [];

    private void CompleteDrainNotice()
    {
        (drainNotice, drainBoxed) = previousDrains.Count > 0 ? previousDrains.Pop() : (null, false);
    }

    internal void CaptureDrainAndWrite(GameString* role, int attribute, int value)
    {
        previousDrains.Push((drainNotice, drainBoxed));
        drainNotice = null;
        drainBoxed = false;
        try
        {
            string name = ReadDrainString(role);
            if (name.Length != 0 && attribute == CumflationPatch.GiAttribute && value == 0)
            {
                drainNotice = new(name, RoleAttribute(role, CumflationPatch.WombAttribute), RoleAttribute(role, CumflationPatch.GiAttribute));
            }
        }
        catch (Exception error) { LogCumflation($"drain snapshot failed: {error.GetType().Name}"); }
        finally { SetRoleAttribute(role, attribute, value); } // Always preserve the native write, exactly once.
    }

    private static string ReadDrainString(GameString* value)
    {
        if (value == null || value->Data == null || value->Length <= 0 || value->Length > value->Capacity) return "";
        int length = value->Length;
        if (value->Data[length - 1] == '\0') length--;
        return new string(value->Data, 0, length);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DrainBinding { internal WeakObject Object; internal ulong FunctionName; }
    [StructLayout(LayoutKind.Sequential)]
    private struct DrainDelegate { internal DrainBinding* Data; internal int Count, Capacity; }

    internal void DispatchDrainCleared(nint nativeDelegate, GameString* role)
    {
        bool forwarded = false;
        try
        {
            var notice = drainNotice;
            if (notice == null || notice.Total <= 0 || notice.Role != ReadDrainString(role)) return;
            nint world = *(nint*)At(SupportedBuild.CurrentWorld);
            WithScriptContext(world, (context, engine) =>
            {
                nint type = FindScriptType(engine, "Core.GUI.Character.UICharacterState", "U_UICharacterState");
                if (type == 0) return false;
                nint uiClass = NativeClassForScriptType(type);
                if (uiClass == 0) { LogCumflation("drain UI unavailable: U_UICharacterState native class missing"); return false; }
                var source = (DrainDelegate*)nativeDelegate;
                if (source == null || source->Count < 0 || source->Count > source->Capacity || (source->Count != 0 && source->Data == null))
                { LogCumflation("drain cleared adapter rejected: invalid native delegate array"); return false; }
                // Original Broadcast compacts/shrinks its input. Give it an owned game-allocator
                // array; never pass managed/stack storage or modify the registered delegate.
                DrainDelegate filtered = default;
                var ui = new List<WeakObject>();
                try
                {
                    if (source->Count > 0)
                    {
                        filtered.Data = (DrainBinding*)((delegate* unmanaged<nuint, uint, nint>)At(SupportedBuild.Allocate))((nuint)source->Count * 16, 8);
                        if (filtered.Data == null) { LogCumflation("drain cleared adapter failed: delegate allocation unavailable"); return false; }
                        filtered.Capacity = source->Count;
                    }
                    for (int i = 0; i < source->Count; i++)
                    {
                        var binding = source->Data[i];
                        if (binding.FunctionName != 0 && IsNativeInstance(ResolveWeak(binding.Object), uiClass))
                        { if (!ui.Any(x => x.Key == binding.Object.Key)) ui.Add(binding.Object); }
                        else filtered.Data[filtered.Count++] = binding;
                    }
                    bool menuOpen = ui.Count > 0 && IsDrainMenuOpen(engine, world);
                    nint clear = 0, progress = 0, button = 0;
                    if (ui.Count > 0)
                    {
                        clear = DrainUiMethod(type, "ClearCurrentCharacterFluidsData", 0);
                        progress = DrainUiMethod(type, "DelaySetFluidProgress", 3);
                        button = DrainUiMethod(type, "SetReleaseFluidsBtnEnabled", 1);
                        if (clear == 0 || progress == 0 || button == 0) return false;
                    }
                    nint menuWidget = 0;
                    foreach (var target in ui)
                    {
                        nint widget = ResolveWeak(target);
                        if (widget == 0) continue;
                        if (!DrainUiReset(context, widget, clear, progress, button)) return false;
                        if (menuWidget == 0) menuWidget = widget;
                    }
                    // Complete every reset before creating a tip, so a later receiver
                    // failure cannot replay the original zero-amount popup over it.
                    if (menuOpen && menuWidget != 0)
                        drainBoxed = ShowDrainMenuAlert(context, engine, type, menuWidget, notice);
                    forwarded = true;
                    ((delegate* unmanaged<nint, GameString*, void>)At(SupportedBuild.Broadcast))((nint)(&filtered), role);
                }
                finally { FreeAllocation(filtered.Data); }
                return true;
            });
        }
        catch (Exception error) { LogCumflation($"drain cleared adapter failed: {error.GetType().Name}"); }
        finally
        {
            if (!forwarded)
            {
                ((delegate* unmanaged<nint, GameString*, void>)At(SupportedBuild.Broadcast))(nativeDelegate, role);
            }
        }
    }

    internal void ShowDrainRightMsg(nint subsystem, byte type, GameString* player, GameString* playerId, GameString* message, GameString* number)
    {
        var notice = drainNotice;
        if (type == SupportedBuild.DrainRightTip && notice != null && drainBoxed)
            return;
        if (type != SupportedBuild.DrainRightTip || notice == null || notice.Total <= 0)
        {
            ((delegate* unmanaged<nint, byte, GameString*, GameString*, GameString*, GameString*, void>)At(SupportedBuild.ShowRightMsg))(subsystem, type, player, playerId, message, number);
            return;
        }
        string amount = notice.Volume;
        fixed (char* chars = amount)
        {
            GameString corrected = new() { Data = chars, Length = amount.Length + 1, Capacity = amount.Length + 1 };
            ((delegate* unmanaged<nint, byte, GameString*, GameString*, GameString*, GameString*, void>)At(SupportedBuild.ShowRightMsg))(subsystem, type, player, playerId, message, &corrected);
        }
    }
}
