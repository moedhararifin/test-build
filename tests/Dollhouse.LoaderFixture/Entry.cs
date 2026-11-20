using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text;
using Dollhouse.Core.Startup;
using Dollhouse.Runtime;

// Analyst-written test DLL only. Never loaded into the game or distributed.
internal static unsafe class Entry
{
    [UnmanagedCallersOnly(EntryPoint = LauncherLoadPlan.EntryExport)]
    public static int Main()
    {
        var marker = Environment.GetEnvironmentVariable("DOLLHOUSE_LOADER_FIXTURE_MARKER")!;
        try
        {
            var original = File.ReadAllBytes(Environment.ProcessPath!);
            nint imageBase = GetModuleHandleW(null);
            var memory = new WindowsMemory(imageBase);
            if (!GetModuleHandleExW(6, (nint)(delegate* unmanaged<int>)&Main, out nint module))
                throw new IOException("Cannot identify the loaded fixture module.");
            char* moduleName = stackalloc char[32768];
            uint length = GetModuleFileNameW(module, moduleName, 32768);
            if (length == 0 || length >= 32768) throw new IOException("Cannot read the fixture module path.");
            string modulePath = new(moduleName, 0, (int)length);
            if (GetModuleHandleW(modulePath) != module)
                throw new IOException("Unicode full-path module lookup did not resolve the loaded fixture.");
            var workspace = LauncherLoadPlan.Restore(original, LauncherLoadPlan.RelativeImportName(Environment.CurrentDirectory, modulePath), (nint)(delegate* unmanaged<int>)&Main,
                memory, memory.ValidateWorkspace);
            // Exercise the actual early reservation after Native AOT initialization,
            // without performing a second allocation or executing any game code.
            var imports = memory.Read(workspace.BaseRva, StartupWorkspace.ImportCapacity);
            int code = workspace.PatchRva + StartupWorkspace.PageSize;
            workspace.StagePatch(memory, new([7, 8, 9], code, [0xb8, 42, 0, 0, 0, 0xc3], []));
            if (((delegate* unmanaged<int>)(imageBase + code))() != 42 ||
                !memory.Read(workspace.BaseRva, imports.Length).AsSpan().SequenceEqual(imports))
                throw new InvalidDataException("Reserved patch workspace failed or modified loader-owned imports.");
            File.WriteAllLines(marker, new[] { "restored", Environment.CurrentDirectory,
                Environment.GetEnvironmentVariable("DOLLHOUSE_TEST_LAUNCH_ENV") ?? "" }
                .Concat(Environment.GetCommandLineArgs().Skip(1).Select(argument => Convert.ToBase64String(Encoding.UTF8.GetBytes(argument)))));
            File.WriteAllText(marker + ".module", modulePath);
            using var pe = new PEReader(new MemoryStream(original, false));
            int result = ((delegate* unmanaged<int>)(imageBase + pe.PEHeaders.PEHeader!.AddressOfEntryPoint))();
            ExitProcess((uint)result);
            return result;
        }
        catch (Exception error)
        {
            File.WriteAllText(marker, error.ToString());
            ExitProcess(91);
            return 91;
        }
    }
    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)] private static extern nint GetModuleHandleW(string? name);
    [DllImport("kernel32.dll", ExactSpelling = true)] private static extern bool GetModuleHandleExW(uint flags, nint address, out nint module);
    [DllImport("kernel32.dll", ExactSpelling = true)] private static extern uint GetModuleFileNameW(nint module, char* name, uint size);
    [DllImport("kernel32.dll", ExactSpelling = true)] private static extern void ExitProcess(uint code);
}
