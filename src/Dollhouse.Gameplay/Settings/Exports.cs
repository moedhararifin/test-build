using System.Runtime.InteropServices;

namespace Dollhouse.Gameplay;

public static unsafe partial class Exports
{
    [UnmanagedCallersOnly(EntryPoint = SettingsBindings.BuildExport)]
    internal static void BuildSettingsMenu(nint menu, nint descriptors)
    { try { Game.BuildSettingsMenu(menu, descriptors); } catch (Exception error) { Report(error); } }

    [UnmanagedCallersOnly(EntryPoint = SettingsBindings.ReadRowExport)]
    internal static int ReadSettingsRow(nint descriptor)
    { try { return Game.ReadSettingsRow(descriptor); } catch (Exception error) { Report(error); return 1; } }

    [UnmanagedCallersOnly(EntryPoint = SettingsBindings.DefaultExport)]
    internal static GameString* DefaultSetting(GameString* result, char* key)
    { try { return Game.DefaultSetting(result, key); } catch (Exception error) { Report(error); if (result != null) *result = default; return result; } }

    [UnmanagedCallersOnly(EntryPoint = SettingsBindings.EffectiveExport)]
    internal static GameString* EffectiveSetting(GameString* result, char* key)
    { try { return Game.EffectiveSetting(result, key); } catch (Exception error) { Report(error); if (result != null) *result = default; return result; } }

    [UnmanagedCallersOnly(EntryPoint = SettingsBindings.SetExport)]
    internal static void SetSetting(char* key, GameString* value)
    { try { Game.SetSetting(key, value); } catch (Exception error) { Report(error); } }

    [UnmanagedCallersOnly(EntryPoint = SettingsBindings.ApplyExport)]
    internal static void ApplySettings(nint menuHook)
    { try { Game.ApplySettings(menuHook); } catch (Exception error) { Report(error); } }
}
