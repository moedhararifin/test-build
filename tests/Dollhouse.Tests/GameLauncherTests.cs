using Dollhouse.App;
using Dollhouse.Core.Startup;

namespace Dollhouse.Tests;

internal static class GameLauncherTests
{
    public static void Verify()
    {
        Check.Equal("Dollhouse", typeof(AppOptions).Assembly.GetName().Name);
        Check.Equal("Dollhouse.Core", typeof(GameLauncher).Assembly.GetName().Name);
        Check.True(typeof(AppOptions).Assembly.GetManifestResourceNames().Contains("Dollhouse.ThirdPartyNotices"), "Renamed app lost its embedded notices.");
        using var temporary = new TemporaryWorkspace();
        var root = Path.Combine(temporary.Paths.Root, "Game folder & Unicode 日本語");
        var target = Path.Combine(root, "Paralogue", "Binaries", "Win64", "Paralogue-Win64-Shipping.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, "Not executable: launch-plan fixture only.");
        var installation = GameInstallation.Locate(target, temporary.Paths.Root, temporary.Paths.Root);
        var start = GameLauncher.CreateStartInfo(installation);
        Check.Equal(target, start.FileName);
        Check.Equal(root, start.WorkingDirectory);
        Check.Equal(Path.Combine(installation.Paths.Local, "settings.json"), start.Environment[Dollhouse.Gameplay.SettingsBindings.PathVariable]);
        Check.True(!start.UseShellExecute, "Game launch must not invoke a shell.");
        Check.Equal("", start.Arguments);
        var expected = new[]
        {
            "-UserDir=" + Path.Combine(root, "Paralogue"), "-NotInstalled", "-nosteam",
            "-DisablePlugins=CacheSignatureVerifier", "-TestSteamCode=REHOSTLOCAL", "-RehostUseSettings",
            "-EngineINI=" + GameLogConfiguration.PathFor(installation)
        };
        Check.True(start.ArgumentList.SequenceEqual(expected), "Launcher must preserve CMD arguments and use the extracted Engine.ini.");
        var extra = new[] { "-windowed", "-SomeValue=hello world", "literal&value", "--debug" };
        Check.True(GameLauncher.CreateStartInfo(installation, extra).ArgumentList.SequenceEqual(expected.Concat(extra)),
            "Game arguments must be appended as individual values, without shell interpretation.");

        Check.True(!AppOptions.Parse([]).NoLaunch, "Default startup must launch the game.");
        Check.True(AppOptions.Parse(["--no-launch"]).NoLaunch, "Server-only startup was not recognized.");
        var debug = AppOptions.Parse(["--debug", "--no-launch", "--target", target]);
        Check.True(debug.Debug && debug.NoLaunch && !debug.Clean, "Debug server-only mode was not preserved.");
        Check.Equal(target, debug.Target);
        var forwarded = AppOptions.Parse([target, "--", .. extra]);
        Check.Equal(target, forwarded.Target);
        Check.True(!forwarded.Debug && forwarded.GameArguments.SequenceEqual(extra), "Arguments after -- were parsed as app options.");
        Check.True(AppOptions.Parse(["--clean"]).Clean, "Cleanup mode was not preserved.");
        Check.Throws<ArgumentException>(() => AppOptions.Parse(["--no-launch", "--no-launch"]));
        Check.Throws<ArgumentException>(() => AppOptions.Parse(["--clean", "--no-launch"]));
        Check.Throws<ArgumentException>(() => AppOptions.Parse(["--clean", "--", "-windowed"]));
        Check.Throws<ArgumentException>(() => AppOptions.Parse(["--no-launch", "--", "-windowed"]));
        Check.Throws<ArgumentException>(() => AppOptions.Parse(["-windowed"]));
        Check.True(!AppOptions.Parse([]).NoGameplayPatches, "Default startup must retain gameplay repairs.");
        var external = AppOptions.Parse(["--debug", "--no-gameplay-patches", "--target", target, "--", "-windowed"]);
        Check.True(external.NoGameplayPatches && external.Debug && external.Target == target, "Licensing-only option was lost.");
        Check.True(external.GameArguments.SequenceEqual(new[] { "-windowed" }), "App option leaked into game arguments.");
        Check.True(!AppOptions.Parse(["--", "--no-gameplay-patches"]).NoGameplayPatches, "Escaped game option was consumed.");
        Check.Throws<ArgumentException>(() => AppOptions.Parse(["--no-gameplay-patches", "--no-gameplay-patches"]));
        Check.Throws<ArgumentException>(() => AppOptions.Parse(["--clean", "--no-gameplay-patches"]));
        Check.Throws<ArgumentException>(() => AppOptions.Parse(["--no-launch", "--no-gameplay-patches"]));
        string variable = RuntimeLaunchOptions.NoGameplayPatchesVariable;
        string? inherited = Environment.GetEnvironmentVariable(variable);
        try
        {
            Environment.SetEnvironmentVariable(variable, "1");
            Check.Equal("0", GameLauncher.CreateStartInfo(installation).Environment[variable]);
            var disabled = GameLauncher.CreateStartInfo(installation, extra, noGameplayPatches: true);
            Check.Equal("1", disabled.Environment[variable]);
            Check.True(disabled.ArgumentList.SequenceEqual(expected.Concat(extra)), "Flag changed native game arguments.");
            Check.Equal("1", Environment.GetEnvironmentVariable(variable)); // Parent value was never changed by the launcher.
        }
        finally { Environment.SetEnvironmentVariable(variable, inherited); }
        // Only CreateStartInfo/Parse are exercised. No call to Start or Process.Start.
    }
}
