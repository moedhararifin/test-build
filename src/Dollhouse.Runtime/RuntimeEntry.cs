using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using Dollhouse.Core.Patching;
using Dollhouse.Core.Patching.Native;
using Dollhouse.Core.Startup;

namespace Dollhouse.Runtime;

internal static unsafe class RuntimeEntry
{
    private static int initialized;
    private static string? logFile;

    [UnmanagedCallersOnly(EntryPoint = LauncherLoadPlan.EntryExport)]
    public static int Main()
    {
        nint originalEntry = 0;
        try
        {
            nint imageBase = GetModuleHandleW(null);
            if (imageBase == 0) throw new InvalidDataException("Cannot resolve the main game image.");
            if (Interlocked.CompareExchange(ref initialized, 1, 0) != 0)
                throw new InvalidOperationException("Runtime initialization was already attempted.");
            var executable = Environment.ProcessPath ?? throw new IOException("Cannot locate the game executable.");
            var installation = GameInstallation.Locate(executable, Path.GetDirectoryName(executable)!, Environment.CurrentDirectory);
            logFile = Path.Combine(installation.Paths.Local, "runtime.log");
            Log("Beginning launcher-controlled runtime initialization.");
            // One original image is needed inside the game for patch recipes. The
            // parent server no longer buffers an EXE or requires a permanent backup.
            var original = File.ReadAllBytes(executable);
            if (original.Length != GameBuild.ExpectedSize || GameBinary.Hash(original) != GameBuild.OriginalSha256)
                throw new InvalidDataException("The game must match the supported original image.");
            using var installedImage = new PEReader(new MemoryStream(original, false));
            var memory = new WindowsMemory(imageBase);
            string runtimePath = RuntimeCompatibility.LibraryPath(installation);
            nint runtimeEntry = (nint)(delegate* unmanaged<int>)&Main;
            var module = GetModuleHandleW(runtimePath);
            if (module == 0) throw new IOException($"Dollhouse runtime is not loaded from '{runtimePath}'.");
            if (GetProcAddress(module, LauncherLoadPlan.EntryExport) != runtimeEntry)
                throw new InvalidDataException($"Runtime entry does not match the loaded module at '{runtimePath}'.");
            var workspace = LauncherLoadPlan.Restore(original, LauncherLoadPlan.RelativeImportName(installation.Root, runtimePath),
                runtimeEntry, memory, memory.ValidateWorkspace);
            Log($"Validated launcher workspace: RVA=0x{workspace.BaseRva:x}, imports={StartupWorkspace.ImportCapacity} bytes, patches={StartupWorkspace.PatchCapacity} bytes; startup handoff restored.");
            bool noGameplayPatches = RuntimeLaunchOptions.ParseNoGameplayPatches(
                Environment.GetEnvironmentVariable(RuntimeLaunchOptions.NoGameplayPatchesVariable));
            var trust = RuntimeTrust.Read(installation.Paths);
            Log("Read existing certificate pin and signing public root; identity files unchanged.");
            // All state paths remain scoped to this installation.
            Environment.SetEnvironmentVariable(DeviceKeyPaths.KeyPathVariable, DeviceKeyPaths.KeyPath(installation));
            Environment.SetEnvironmentVariable(Dollhouse.Gameplay.SettingsBindings.PathVariable, Path.Combine(installation.Paths.Local, "settings.json"));
            Environment.SetEnvironmentVariable(Dollhouse.Gameplay.CameraBindings.PathVariable, CameraConfiguration.PathFor(installation));
            nint Resolve(string name)
            {
                var pointer = GetProcAddress(module, name);
                if (pointer == 0) throw new InvalidDataException($"Runtime export is missing: {name}.");
                return pointer;
            }
            var nativeCrypto = GetModuleHandleW("ncrypt.dll");
            var selection = RuntimePatchPlan.Build(original, trust,
                name => GetProcAddress(nativeCrypto, name), Resolve, workspace.PatchRva, noGameplayPatches);
            if (selection.Gameplay is { } layout) workspace.StagePatch(memory, layout);
            MemoryPatchTransaction.Apply(memory, selection.Edits, Log);
            Log($"Runtime initialized; {selection.Edits.Count} memory edits; connection/licensing patches enabled; gameplay patches {(noGameplayPatches ? "disabled" : "enabled")}.");
            // Both partitions remain owned until process exit. Loader references and
            // game code must never outlive their respective parts of the reservation.
            originalEntry = imageBase + installedImage.PEHeaders.PEHeader!.AddressOfEntryPoint;

        }
        catch (Exception error)
        {
            Log($"Startup failed: {error.GetType().Name}: {error.Message}");
            MessageBoxW(0, "Dollhouse could not initialize. Start the game through Dollhouse.exe to verify the installation.\n\n" + error.Message,
                "Dollhouse runtime", 0x10);
            ExitProcess(0xd011);
            return 0xd011;
        }
        // Native AOT supplies this entry wrapper's ABI/unwind metadata. The
        // original CRT runs only after restoration and successful patching.
        int result = ((delegate* unmanaged<int>)originalEntry)();
        ExitProcess(unchecked((uint)result));
        return result;
    }

    private static void Log(string message)
    {
        try
        {
            if (logFile is not null && Directory.Exists(Path.GetDirectoryName(logFile)))
                File.AppendAllText(logFile, $"{DateTime.UtcNow:O} [Dollhouse runtime] {message}{Environment.NewLine}");
        }
        catch { /* No new state directories or startup dependencies for logging. */ }
        try { Console.Error.WriteLine("[Dollhouse runtime] " + message); }
        catch { /* A GUI game need not have a console. */ }
        try { OutputDebugStringW("[Dollhouse runtime] " + message); }
        catch { /* Diagnostics cannot change the native startup result. */ }
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern nint GetModuleHandleW(string? name);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)] private static extern nint GetProcAddress(nint module, string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern void OutputDebugStringW(string text);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern int MessageBoxW(nint owner, string text, string caption, uint flags);
    [DllImport("kernel32.dll", ExactSpelling = true)] private static extern void ExitProcess(uint code);
}
