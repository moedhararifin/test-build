using System.Runtime.InteropServices;

namespace Dollhouse.Gameplay;

internal sealed unsafe partial class NativeGame
{
    [StructLayout(LayoutKind.Explicit, Size = SettingsBindings.RowSize)]
    private struct SettingsRow
    {
        [FieldOffset(0)] internal nint Key;
        [FieldOffset(8)] internal nint Label;
        [FieldOffset(16)] internal byte Kind;
        [FieldOffset(17)] internal byte Restart;
    }
    private nint settingsRows;
    private GameplaySettingsStore? gameplaySettings;
    private bool settingsLoaded;
    // Process-lifetime immutable strings. Native descriptors borrow them; no managed pin escapes a call.
    private static readonly nint CumflationSettingKey = Marshal.StringToHGlobalUni(SettingsBindings.CumflationKey);
    private static readonly nint CumflationSettingLabel = Marshal.StringToHGlobalUni(SettingsBindings.CumflationLabel);
    private static readonly nint IgnoreRestrictionsSettingKey = Marshal.StringToHGlobalUni(SettingsBindings.IgnoreRestrictionsKey);
    private static readonly nint IgnoreRestrictionsSettingLabel = Marshal.StringToHGlobalUni(SettingsBindings.IgnoreRestrictionsLabel);
    private static readonly nint FatigueSettingKey = Marshal.StringToHGlobalUni(SettingsBindings.FatigueKey);
    private static readonly nint FatigueSettingLabel = Marshal.StringToHGlobalUni(SettingsBindings.FatigueLabel);

    private void LogSettings(string message) => LogGameplay("settings", message);
    private GameplaySettingsStore? Settings
    {
        get
        {
            if (settingsLoaded) return gameplaySettings;
            settingsLoaded = true;
            string? path = Environment.GetEnvironmentVariable(SettingsBindings.PathVariable);
            if (string.IsNullOrWhiteSpace(path)) { LogSettings("path unavailable; default gameplay settings used and saving disabled"); return null; }
            try { gameplaySettings = new(path, LogSettings); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            { LogSettings($"store unavailable: {error.GetType().Name}; saving disabled"); }
            return gameplaySettings;
        }
    }
    private bool CumflationEnabled => Settings?.Current.CumflationEnabled ?? true;
    private bool IgnoreCumflationRestrictions => Settings?.Current.IgnoreCumflationRestrictions ?? false;
    private bool FatigueEnabled => Settings?.Current.FatigueEnabled ?? true;

    internal void BuildSettingsMenu(nint menu, nint storage)
    {
        if (menu == 0 || storage == 0) { LogSettings("menu build rejected: null widget/storage"); return; }
        if (settingsRows == 0)
        {
            var source = (SettingsRow*)At(SettingsBindings.NativeRows);
            for (int i = 0; i < SettingsBindings.OriginalRowCount; i++)
                if (source[i].Key == 0 || source[i].Label == 0)
                { LogSettings($"menu initialization rejected: original row {i} is not initialized"); return; }
            // Copy AFTER native static initialization and OS relocations. The original table
            // is never modified, and no absolute image address is serialized into the patch.
            var destination = (SettingsRow*)storage;
            new ReadOnlySpan<SettingsRow>(source, SettingsBindings.OriginalRowCount)
                .CopyTo(new Span<SettingsRow>(destination, SettingsBindings.OriginalRowCount));
            destination[SettingsBindings.OriginalRowCount] = new()
            { Key = CumflationSettingKey, Label = CumflationSettingLabel, Kind = 0, Restart = 0 };
            destination[SettingsBindings.OriginalRowCount + 1] = new()
            { Key = IgnoreRestrictionsSettingKey, Label = IgnoreRestrictionsSettingLabel, Kind = 0, Restart = 0 };
            destination[SettingsBindings.OriginalRowCount + 2] = new()
            { Key = FatigueSettingKey, Label = FatigueSettingLabel, Kind = 0, Restart = 0 };
            settingsRows = storage;
        }
        if (settingsRows != storage) { LogSettings("menu build rejected: descriptor storage changed"); return; }
        ((delegate* unmanaged<nint, void>)At(SettingsBindings.Build))(menu);
    }

    internal int ReadSettingsRow(nint descriptor)
    {
        if (settingsRows != 0 && descriptor == settingsRows + SettingsBindings.OriginalRowCount * SettingsBindings.RowSize)
        {
            return CumflationEnabled ? 1 : 0;
        }
        if (settingsRows != 0 && descriptor == settingsRows + (SettingsBindings.OriginalRowCount + 1) * SettingsBindings.RowSize)
            return IgnoreCumflationRestrictions ? 1 : 0;
        if (settingsRows != 0 && descriptor == settingsRows + (SettingsBindings.OriginalRowCount + 2) * SettingsBindings.RowSize)
            return FatigueEnabled ? 1 : 0;
        return ((delegate* unmanaged<nint, int>)At(SettingsBindings.ReadRow))(descriptor);
    }

    private static bool IsCumflationSetting(char* key) => key != null && new string(key) == SettingsBindings.CumflationKey;
    private static bool IsIgnoreRestrictionsSetting(char* key) => key != null && new string(key) == SettingsBindings.IgnoreRestrictionsKey;
    private static bool IsFatigueSetting(char* key) => key != null && new string(key) == SettingsBindings.FatigueKey;
    private GameString* SettingString(GameString* destination, bool value)
    {
        if (destination == null) return null;
        string text = value ? "1" : "0";
        fixed (char* chars = text)
        {
            GameString source = new() { Data = chars, Length = 2, Capacity = 2 };
            *destination = default; // Native FString return ABI: caller supplies uninitialized result storage.
            Copy(destination, &source);
        }
        return destination;
    }

    internal GameString* DefaultSetting(GameString* destination, char* key)
    {
        if (IsFatigueSetting(key)) return SettingString(destination, true);
        if (IsIgnoreRestrictionsSetting(key)) return SettingString(destination, false);
        if (!IsCumflationSetting(key))
            return ((delegate* unmanaged<GameString*, char*, GameString*>)At(SettingsBindings.Default))(destination, key);
        return SettingString(destination, true);
    }

    internal GameString* EffectiveSetting(GameString* destination, char* key)
    {
        if (IsFatigueSetting(key)) return SettingString(destination, FatigueEnabled);
        if (IsIgnoreRestrictionsSetting(key)) return SettingString(destination, IgnoreCumflationRestrictions);
        if (!IsCumflationSetting(key))
            return ((delegate* unmanaged<GameString*, char*, GameString*>)At(SettingsBindings.Effective))(destination, key);
        return SettingString(destination, CumflationEnabled);
    }

    internal void SetSetting(char* key, GameString* value)
    {
        bool ignore = IsIgnoreRestrictionsSetting(key);
        bool fatigue = IsFatigueSetting(key);
        if (!ignore && !fatigue && !IsCumflationSetting(key))
        { ((delegate* unmanaged<char*, GameString*, void>)At(SettingsBindings.Set))(key, value); return; }
        if (value == null || value->Data == null || value->Length != 2 || value->Capacity < 2 ||
            value->Data[1] != '\0' || value->Data[0] is not ('0' or '1'))
        { LogSettings("Apply rejected: gameplay toggle value must be 0 or 1"); return; }
        bool enabled = value->Data[0] == '1';
        var store = Settings;
        if (store == null || !store.Commit(fatigue
            ? store.Current with { FatigueEnabled = enabled }
            : ignore
            ? store.Current with { IgnoreCumflationRestrictions = enabled }
            : store.Current with { CumflationEnabled = enabled }))
        { LogSettings("Apply failed: previous gameplay value retained; row will refresh to committed value"); return; }
        if (ignore && enabled && eligibilityEngine == 0)
            LogSettings("restriction override saved but guard unavailable; original card restrictions remain active");
    }

    internal void ApplySettings(nint menuHook)
    {
        try { ((delegate* unmanaged<nint, void>)At(SettingsBindings.Apply))(menuHook); }
        finally
        {
            // Native Apply ignores Set's I/O result and updates its baseline regardless.
            // Re-read committed values, including our unchanged value after a failed save.
            ((delegate* unmanaged<nint, void>)At(SettingsBindings.Refresh))(menuHook);
        }
    }
}
