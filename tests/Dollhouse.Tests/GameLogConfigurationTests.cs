using Dollhouse.Core.Startup;

namespace Dollhouse.Tests;

internal static class GameLogConfigurationTests
{
    public static void Verify()
    {
        using var temporary = new TemporaryWorkspace();
        var installation = new GameInstallation(Path.Combine(temporary.Paths.Root, "unused.exe"), temporary.Paths.Root);
        var path = GameLogConfiguration.PathFor(installation);
        Check.Equal(Path.Combine(installation.Root, ".dollhouse", "config", "Engine.ini"), path);
        var previousConfig = Path.Combine(installation.Root, ".paralogue", "config", "Engine.ini");
        Directory.CreateDirectory(Path.GetDirectoryName(previousConfig)!);
        File.WriteAllText(previousConfig, "historical state: do not migrate");
        var saved = Path.Combine(installation.Root, "Paralogue", "Saved");
        Check.True(!Directory.Exists(saved), "Test must start without Saved.");
        Check.True(GameLogConfiguration.Prepare(installation), "Bundled Engine.ini must be extracted on first startup.");
        Check.Equal("historical state: do not migrate", File.ReadAllText(previousConfig));
        Check.Equal("[Core.Log]\nLogStreamline=Fatal\n", File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal));
        var bytes = File.ReadAllBytes(path);
        Check.True(!Directory.Exists(saved), "Extraction must not create Saved.");
        var unchangedTime = File.GetLastWriteTimeUtc(path);
        Check.True(!GameLogConfiguration.Prepare(installation), "Repeat startup must reuse identical extracted bytes.");
        Check.Equal(unchangedTime, File.GetLastWriteTimeUtc(path));

        // This is a managed resource, not the user's editable Saved configuration.
        File.WriteAllText(path, "stale or damaged configuration");
        Check.True(GameLogConfiguration.Prepare(installation), "Startup must repair a stale extracted resource.");
        Check.True(File.ReadAllBytes(path).SequenceEqual(bytes), "Repair did not restore exact resource bytes.");
        var savedIni = Path.Combine(saved, "Config", "Windows", "Engine.ini");
        Directory.CreateDirectory(Path.GetDirectoryName(savedIni)!);
        File.WriteAllText(savedIni, "existing game settings");
        GameLogConfiguration.Prepare(installation);
        Check.Equal("existing game settings", File.ReadAllText(savedIni));
        // Simulate the game's removal of Saved, confined to this disposable fixture.
        Check.True(Path.GetFullPath(saved).StartsWith(temporary.Paths.Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Unsafe test cleanup.");
        Directory.Delete(saved, recursive: true);
        Check.True(File.ReadAllBytes(path).SequenceEqual(bytes), "Removing Saved removed the managed configuration.");
        File.Delete(path);
        Check.True(GameLogConfiguration.Prepare(installation), "Startup must recreate the resource after cleanup.");
        Check.True(File.ReadAllBytes(path).SequenceEqual(bytes), "Recreated configuration differs from the bundled resource.");
    }
}
