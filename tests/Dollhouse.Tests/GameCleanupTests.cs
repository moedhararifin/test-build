using Dollhouse.App;
using Dollhouse.Core.Startup;
using Dollhouse.Core.Patching;

namespace Dollhouse.Tests;

internal static class GameCleanupTests
{
    public static void Verify()
    {
        Check.True(AppOptions.Parse(["--clean"]).Clean, "Clean flag was not recognized.");
        Check.True(!AppOptions.Parse([]).Clean, "Normal startup selected cleanup.");
        Check.Equal("game.exe", AppOptions.Parse(["--clean", "--target", "game.exe"]).Target);
        Check.Equal("game.exe", AppOptions.Parse(["game.exe", "--clean"]).Target);
        Check.Throws<ArgumentException>(() => AppOptions.Parse(["--debug", "--clean"]));
        Check.Throws<ArgumentException>(() => AppOptions.Parse(["--clean", "--debug"]));
        Check.Throws<ArgumentException>(() => AppOptions.Parse(["--clean", "--clean"]));

        using var temporary = new TemporaryWorkspace();
        var root = Path.Combine(temporary.Paths.Root, "game");
        var appData = Path.Combine(temporary.Paths.Root, "appdata");
        var installation = new GameInstallation(Path.Combine(root, "Paralogue", "Binaries", "Win64", "game.exe"), root);
        var cache = Path.Combine(temporary.Paths.Root, "override", "pakkey.bin");
        var targets = GameCleanup.Plan(installation, appData, cache);
        foreach (var target in targets)
            Write(target.Directory ? Path.Combine(target.Path, "nested", "generated.bin") : target.Path, "generated");
        var preserved = new[]
        {
            installation.Executable, installation.Executable + ".bak",
            Path.Combine(root, "Paralogue", "Binaries", "Win64", "game.backup.exe"),
            Path.Combine(root, "Paralogue", "Content", "Paks", "content.pak"),
            Path.Combine(appData, "UnrealEngine", "Common", "DerivedDataCache", "shared.bin"),
            Path.Combine(appData, "AnotherGame", "Saved", "save.bin")
        };
        foreach (var file in preserved) Write(file, "preserved");
        var messages = new List<string>();
        GameCleanup.DeleteTargets(targets, messages.Add);
        foreach (var target in targets)
            Check.True(!File.Exists(target.Path) && !Directory.Exists(target.Path), "Generated data survived cleanup.");
        foreach (var file in preserved) Check.Equal("preserved", File.ReadAllText(file));
        GameCleanup.DeleteTargets(targets, messages.Add);
        Check.True(messages.Any(message => message.StartsWith("Already absent:", StringComparison.Ordinal)), "Repeat cleanup was not idempotent.");
        Check.Throws<InvalidOperationException>(() => GameCleanup.Plan(installation, appData, installation.Executable));
        Check.Throws<InvalidOperationException>(() => GameCleanup.Plan(installation, appData, Path.Combine(root, "Paralogue", "Content", "installed.bin")));
        Check.Throws<InvalidOperationException>(() => GameCleanup.Plan(installation, appData, Path.Combine(root, ".dollhouse", "license", "key.bin")));

        // A bad later target must be discovered before an earlier target is deleted.
        Write(targets[0].Path + "/keep.bin", "keep");
        var wrongType = Path.Combine(temporary.Paths.Root, "not-a-cache.bin");
        Directory.CreateDirectory(wrongType);
        Check.Throws<IOException>(() => GameCleanup.DeleteTargets([targets[0], new(wrongType, false)]));
        Check.Equal("keep", File.ReadAllText(targets[0].Path + "/keep.bin"));
        Check.Throws<IOException>(() => GameCleanup.DeleteTargets([new(Path.GetPathRoot(root)!, true)]));
        Check.Throws<IOException>(() => GameCleanup.DeleteTargets([new(root + "\\..", true)]));

        // Junctions require no developer-mode privilege on Windows. Never traverse them.
        if (OperatingSystem.IsWindows())
        {
            var outside = Path.Combine(temporary.Paths.Root, "outside");
            Write(Path.Combine(outside, "keep.bin"), "keep");
            var link = Path.Combine(targets[0].Path, "redirect");
            var start = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "/d", "/c", "mklink", "/J", link, outside }) start.ArgumentList.Add(argument);
            using var process = System.Diagnostics.Process.Start(start)!;
            process.WaitForExit();
            Check.True(process.ExitCode == 0, "Cannot create disposable junction fixture: " + process.StandardError.ReadToEnd());
            try
            {
                Check.Throws<IOException>(() => GameCleanup.DeleteTargets(targets));
                Check.Equal("keep", File.ReadAllText(Path.Combine(outside, "keep.bin")));
                Check.Equal("keep", File.ReadAllText(targets[0].Path + "/keep.bin"));
            }
            finally { Directory.Delete(link); }
        }
        VerifyRestoreBeforeDeletion();
    }

    private static void VerifyRestoreBeforeDeletion()
    {
        var original = GameBinaryFixture.Original;
        if (original is null)
        {
            Console.WriteLine("SKIP cleanup restore checks: supply DOLLHOUSE_TEST_GAME or .local/fixtures/Paralogue-Win64-Shipping.exe.");
            return;
        }
        using var temporary = new TemporaryWorkspace();
        var target = Path.Combine(temporary.Paths.Root, "fixture.exe");
        File.Copy(original, target);
        var installation = new GameInstallation(target, temporary.Paths.Root);
        var backup = GameBinary.BackupPath(target);
        File.Copy(original, backup);
        File.WriteAllText(target, "modified old game fixture");
        var modifiedHash = GameBinary.Hash(File.ReadAllBytes(target));
        var targets = GameCleanup.Plan(installation, Path.Combine(temporary.Paths.Root, "appdata"));
        var tls = Path.Combine(installation.Root, ".dollhouse", "tls", "server.key");
        var license = Path.Combine(installation.Root, ".dollhouse", "license", "authority.json");
        Write(tls, "test TLS");
        Write(license, "test license");

        // Exclusive-open failure must leave both identities intact.
        using (var locked = File.Open(target, FileMode.Open, FileAccess.Read, FileShare.None))
            Check.Throws<IOException>(() => GameCleanup.RestoreAndDelete(installation, targets));
        Check.Equal("test TLS", File.ReadAllText(tls));
        Check.Equal("test license", File.ReadAllText(license));

        // Invalid backup must not delete identities or change the patched executable.
        File.WriteAllText(backup, "invalid backup");
        Check.Throws<InvalidDataException>(() => GameCleanup.RestoreAndDelete(installation, targets));
        Check.Equal(modifiedHash, GameBinary.Hash(File.ReadAllBytes(target)));
        Check.True(File.Exists(tls) && File.Exists(license), "Failed restoration deleted identities.");
        File.Copy(original, backup, overwrite: true);

        GameCleanup.RestoreAndDelete(installation, targets, message =>
        {
            if (message.StartsWith("Deleted:", StringComparison.Ordinal))
                Check.Equal(GameBuild.OriginalSha256, GameBinary.Hash(File.ReadAllBytes(target)));
        });
        Check.Equal(GameBuild.OriginalSha256, GameBinary.Hash(File.ReadAllBytes(target)));
        Check.True(!File.Exists(backup), "Cleanup retained the verified redundant backup.");
        Check.True(!File.Exists(tls) && !File.Exists(license), "Cleanup retained TLS/license state.");
        var startup = new List<string>();
        var diagnostics = new List<string>();
        var regenerated = GamePreparation.Prepare(installation, startup.Add, diagnostics.Add);
        Check.True(startup.Any(line => line.Contains("Local license issuer created", StringComparison.Ordinal)), "Startup did not report new license signing keys.");
        Check.True(startup.All(line => !line.Contains(installation.Root, StringComparison.Ordinal)), "Normal startup contains verbose artifact paths.");
        Check.True(!File.Exists(backup), "Startup recreated a binary backup.");
        var reusedStartup = new List<string>();
        GamePreparation.Prepare(installation, reusedStartup.Add);
        Check.True(reusedStartup.Any(line => line.Contains("Local license issuer ready", StringComparison.Ordinal)), "Repeat startup did not report reused license signing keys.");
        Check.True(File.Exists(GameLogConfiguration.PathFor(installation)), "Normal startup must extract the bundled game config after cleanup.");
        Check.True(File.Exists(regenerated.Paths.Certificate) && File.Exists(license), "Normal startup failed to regenerate identities after cleanup.");
        Check.Equal(regenerated.LicenseAuthority.RootPublicKey,
            RuntimeTrust.Read(installation.Paths).LicenseRoot);
        using (GameBinary.OpenVerifiedOriginal(target)) { }
        GameCleanup.RestoreAndDelete(installation, targets);
        Check.True(!File.Exists(GameLogConfiguration.PathFor(installation)), "Cleanup must remove the extracted game config.");
        File.Delete(backup);
        GameCleanup.RestoreAndDelete(installation, targets); // A pristine EXE needs no backup to reset data.
    }

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }
}
