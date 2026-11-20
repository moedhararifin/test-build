using System.Diagnostics;
using Dollhouse.Core.Patching;

namespace Dollhouse.Core.Startup;

public static class GameLauncher
{
    public static ProcessStartInfo CreateStartInfo(GameInstallation installation, IEnumerable<string>? arguments = null, bool noGameplayPatches = false)
    {
        var start = new ProcessStartInfo(installation.Executable)
        {
            WorkingDirectory = installation.Root,
            UseShellExecute = false
        };
        start.Environment[DeviceKeyPaths.KeyPathVariable] = DeviceKeyPaths.KeyPath(installation);
        start.Environment[Dollhouse.Core.Patching.SettingsBindings.PathVariable] = Path.Combine(installation.Paths.Local, "settings.json");
        // Always override inherited values; the choice applies to this launch only.
        start.Environment[RuntimeLaunchOptions.NoGameplayPatchesVariable] = noGameplayPatches ? "1" : "0";
        // Match Fallen Doll.cmd without executing a shell or the downloaded script.
        foreach (var argument in new[]
        {
            "-UserDir=" + installation.ProjectDirectory,
            "-NotInstalled", "-nosteam", "-DisablePlugins=CacheSignatureVerifier",
            "-TestSteamCode=REHOSTLOCAL", "-RehostUseSettings",
            "-EngineINI=" + GameLogConfiguration.PathFor(installation)
        }) start.ArgumentList.Add(argument);
        foreach (var argument in arguments ?? []) start.ArgumentList.Add(argument);
        return start;
    }

    public static Process Start(PreparedGame prepared, IEnumerable<string>? arguments = null, bool noGameplayPatches = false)
    {
        using var original = prepared.TakeOriginalBinary();
        var installation = prepared.Installation;
        return SuspendedGameLauncher.Start(CreateStartInfo(installation, arguments, noGameplayPatches), original, RuntimeCompatibility.LibraryPath(installation));
    }
}
