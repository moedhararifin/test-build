using System.Diagnostics;
using System.Buffers.Binary;
using System.Reflection.PortableExecutable;
using Dollhouse.Core.Patching.Native;
using Dollhouse.Core.Startup;

namespace Dollhouse.Tests;

internal static class LauncherLoadingTests
{
    internal static void Verify()
    {
        StartupWorkspaceTests.Verify();
        Check.Equal("\"Game.exe\" -nosteam -TestSteamCode=REHOSTLOCAL",
            WindowsCommandLine.Build("Game.exe", ["-nosteam", "-TestSteamCode=REHOSTLOCAL"]).ToString());
        Check.Throws<ArgumentException>(() => WindowsCommandLine.Build("Game.exe", ["hidden\0argument"]));
        using var workspace = new TemporaryWorkspace();
        string gameRoot = Path.Combine(workspace.Paths.Root, "Launcher fixture 日本語 中文 한국어 😀 & spaces");
        string directory = Path.Combine(gameRoot, "Paralogue", "Binaries", "Win64");
        Directory.CreateDirectory(directory);
        var executable = Path.Combine(directory, "DollhouseLoader_" + Guid.NewGuid().ToString("N") + ".exe");
        string runtimeDirectory = Path.Combine(gameRoot, ".dollhouse", "runtime");
        Directory.CreateDirectory(runtimeDirectory);
        var dll = Path.Combine(runtimeDirectory, "dhruntime.dll");
        var shadow = Path.Combine(directory, "dhruntime.dll");
        File.WriteAllBytes(shadow, RuntimeLoaderFixture.Library(52, LauncherLoadPlan.EntryExport));
        var original = RuntimeLoaderFixture.Executable();
        File.WriteAllBytes(executable, original);
        File.WriteAllBytes(dll, RuntimeLoaderFixture.Library(41, LauncherLoadPlan.EntryExport));
        using var child = SuspendedGameLauncher.Start(new ProcessStartInfo(executable)
        { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true }, original, dll);
        if (!child.WaitForExit(10000)) { child.Kill(); throw new Exception("Own launcher fixture timed out."); }
        Check.Equal(41, child.ExitCode);
        Check.True(File.Exists(shadow), "Loading must select the requested path without modifying other DLLs.");
        Check.True(original.AsSpan().SequenceEqual(File.ReadAllBytes(executable)), "Launcher must leave disk bytes unchanged.");
        // Reuse a prepared handle. This deliberately synthetic EXE would fail the
        // supported-game hash if GameLauncher tried to verify it a second time.
        var verifiedHandle = File.Open(executable, FileMode.Open, FileAccess.Read, FileShare.Read);
        using (var prepared = new PreparedGame(new(executable, gameRoot), workspace.Paths, "", null!)
            { OriginalBinary = verifiedHandle })
        {
            using var reused = GameLauncher.Start(prepared);
            if (!reused.WaitForExit(10000)) { reused.Kill(); throw new Exception("Prepared-handle fixture timed out."); }
            Check.Equal(41, reused.ExitCode);
            Check.True(!verifiedHandle.CanRead, "Launch must close the consumed preparation handle.");
            Check.Throws<InvalidOperationException>(() => prepared.TakeOriginalBinary());
        }
        var startInfo = new ProcessStartInfo(executable) { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true };
        var mismatch = (byte[])original.Clone(); mismatch[2] = 1;
        Check.Throws<InvalidDataException>(() => SuspendedGameLauncher.Start(startInfo, mismatch, dll));
        var orphaned = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executable));
        try { Check.Equal(0, orphaned.Length); } finally { foreach (var process in orphaned) process.Dispose(); }
        File.WriteAllBytes(dll, RuntimeLoaderFixture.Library(41, "WrongExport"));
        Check.Throws<InvalidDataException>(() => SuspendedGameLauncher.Start(startInfo, original, dll));
        File.Delete(dll);
        Check.Throws<FileNotFoundException>(() => SuspendedGameLauncher.Start(startInfo, original, dll));
        // A direct launch remains the original program, even with a DLL present.
        File.WriteAllBytes(dll, RuntimeLoaderFixture.Library(41, LauncherLoadPlan.EntryExport));
        using (var direct = Process.Start(startInfo)!)
        {
            if (!direct.WaitForExit(10000)) { direct.Kill(); throw new Exception("Direct own fixture timed out."); }
            Check.Equal(73, direct.ExitCode);
        }
        string compiled = Path.Combine(Dollhouse.Core.WorkspacePaths.Discover().Root, "artifacts", "loader-fixture", "dhloaderfixture.dll");
        if (File.Exists(compiled))
        {
            string compiledCopy = Path.Combine(runtimeDirectory, "dhloaderfixture.dll");
            File.Copy(compiled, compiledCopy);
            var start = new ProcessStartInfo(executable) { WorkingDirectory = gameRoot, UseShellExecute = false, CreateNoWindow = true };
            var marker = Path.Combine(directory, "result.txt");
            start.Environment["DOLLHOUSE_LOADER_FIXTURE_MARKER"] = marker;
            const string environment = "literal=日本語 & %TEMP%";
            start.Environment["DOLLHOUSE_TEST_LAUNCH_ENV"] = environment;
            var arguments = new[] { "", "two words", "quote\"inside", "trailing\\", "\\\\server\\folder with spaces\\",
                "日本語 中文 한국어 😀 & %TEMP% $(literal) !", "-nosteam", "tab\there", "line\nbreak", "\\\"", "\\\\\"", " space\\\\", "\"\"" };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var restored = SuspendedGameLauncher.Start(start, original, compiledCopy);
            if (!restored.WaitForExit(15000)) { restored.Kill(); throw new Exception("Compiled launcher fixture timed out."); }
            if (restored.ExitCode != 73) throw new Exception("Compiled startup failed: " + (File.Exists(marker) ? File.ReadAllText(marker) : restored.ExitCode.ToString()));
            var result = File.ReadAllLines(marker);
            Check.Equal("restored", result[0]);
            Check.Equal(gameRoot, result[1]);
            Check.True(StringComparer.OrdinalIgnoreCase.Equals(compiledCopy, File.ReadAllText(marker + ".module")), "Compiled fixture loaded from the wrong directory.");
            Check.Equal(environment, result[2]);
            Check.True(result.Skip(3).Select(value => System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(value))).SequenceEqual(arguments),
                "Native command-line escaping must preserve every argument exactly.");
            Check.True(original.AsSpan().SequenceEqual(File.ReadAllBytes(executable)), "Compiled startup must preserve disk bytes.");
            var apphost = Path.Combine(AppContext.BaseDirectory, "Dollhouse.Tests.exe");
            if (File.Exists(apphost))
            {
                // The SDK apphost restricts imports to System32 (0x800), unlike
                // the game's normal search policy (0). Adapt only an isolated
                // copy of our own fixture; never relax the game's load policy.
                // https://learn.microsoft.com/en-us/cpp/build/reference/dependentloadflag
                var sdkBytes = File.ReadAllBytes(apphost);
                var hostBytes = (byte[])sdkBytes.Clone();
                using (var pe = new PEReader(new MemoryStream(hostBytes, false)))
                using (var image = new PeImage(hostBytes))
                {
                    var config = pe.PEHeaders.PEHeader!.LoadConfigTableDirectory;
                    Check.True(config.Size >= 80, "The SDK fixture must expose DependentLoadFlags.");
                    int flags = image.FileOffset(config.RelativeVirtualAddress) + 78;
                    Check.Equal((ushort)0x800, BinaryPrimitives.ReadUInt16LittleEndian(hostBytes.AsSpan(flags)));
                    BinaryPrimitives.WriteUInt16LittleEndian(hostBytes.AsSpan(flags), 0);
                }
                string hostDirectory = Path.Combine(gameRoot, "SDK apphost 中文 한국어 😀");
                Directory.CreateDirectory(hostDirectory);
                foreach (var file in Directory.GetFiles(AppContext.BaseDirectory))
                    File.Copy(file, Path.Combine(hostDirectory, Path.GetFileName(file)));
                string hostExecutable = Path.Combine(hostDirectory, Path.GetFileName(apphost));
                File.WriteAllBytes(hostExecutable, hostBytes);
                var hostDll = compiledCopy;
                File.WriteAllBytes(Path.Combine(hostDirectory, "dhloaderfixture.dll"), RuntimeLoaderFixture.Library(53, LauncherLoadPlan.EntryExport));
                File.Copy(compiled, hostDll, true);
                var managedOutput = Path.Combine(directory, "managed-result.txt");
                var hostStart = new ProcessStartInfo(hostExecutable) { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true };
                hostStart.Environment["DOLLHOUSE_LOADER_FIXTURE_MARKER"] = marker;
                foreach (var argument in new[] { "--launcher-managed-child", managedOutput }.Concat(arguments)) hostStart.ArgumentList.Add(argument);
                using (var plainHost = Process.Start(hostStart)!)
                {
                    if (!plainHost.WaitForExit(20000)) { plainHost.Kill(); throw new Exception("Unmodified apphost timed out."); }
                    if (plainHost.ExitCode != 0) throw new Exception($"SDK apphost fixture is not runnable (0x{plainHost.ExitCode:x8}).");
                }
                // Independent baseline: let .NET ArgumentList serialize and
                // launch the child, then compare its actual received arguments.
                var baseline = ReadArguments(managedOutput);
                Check.True(baseline.SequenceEqual(arguments), ".NET ArgumentList baseline must preserve arguments.");
                File.Delete(managedOutput);
                File.WriteAllBytes(hostDll, RuntimeLoaderFixture.Library(41, LauncherLoadPlan.EntryExport));
                using (var stubHost = SuspendedGameLauncher.Start(hostStart, hostBytes, hostDll))
                {
                    if (!stubHost.WaitForExit(20000)) { stubHost.Kill(); throw new Exception("Apphost import stub timed out."); }
                    if (stubHost.ExitCode != 41) throw new Exception($"Apphost import stub failed (0x{stubHost.ExitCode:x8}).");
                }
                File.Copy(compiled, hostDll, true);
                using var host = SuspendedGameLauncher.Start(hostStart, hostBytes, hostDll);
                if (!host.WaitForExit(20000)) { host.Kill(); throw new Exception("Managed apphost startup timed out."); }
                if (host.ExitCode != 0) throw new Exception($"Managed apphost startup failed (0x{host.ExitCode:x8}): " + File.ReadAllText(marker));
                Check.True(ReadArguments(managedOutput).SequenceEqual(baseline), "Suspended launch must match .NET ArgumentList exactly.");
                Check.True(StringComparer.OrdinalIgnoreCase.Equals(hostDll, File.ReadAllText(marker + ".module")), "SDK fixture loaded the shadow runtime instead of the requested Unicode installation's runtime.");
                Check.True(File.ReadAllBytes(hostExecutable).AsSpan().SequenceEqual(hostBytes), "Fixture launch must leave disk bytes unchanged.");
                Check.True(File.ReadAllBytes(apphost).AsSpan().SequenceEqual(sdkBytes), "The original SDK apphost must remain unchanged.");
            }
        }
        else Console.WriteLine("SKIP compiled startup: publish tests/Dollhouse.LoaderFixture to artifacts/loader-fixture first.");
    }

    private static string[] ReadArguments(string path) => File.ReadAllLines(path)
        .Select(value => System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(value))).ToArray();
}
