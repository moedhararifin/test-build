using System.ComponentModel;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text;
using Dollhouse.Core.Patching.Native;

namespace Dollhouse.Core.Startup;

internal static class SuspendedGameLauncher
{
    internal static Process Start(ProcessStartInfo start, byte[] original, string runtimePath)
    {
        using var stream = new MemoryStream(original, false);
        return Start(start, stream, runtimePath);
    }

    internal static Process Start(ProcessStartInfo start, Stream original, string runtimePath)
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
            throw new PlatformNotSupportedException("Game loading requires a Windows x64 process (including Wine/Proton).");
        runtimePath = Path.GetFullPath(runtimePath);
        if (!File.Exists(runtimePath)) throw new FileNotFoundException("Dollhouse runtime is missing. Start through Dollhouse.exe to restore it.", runtimePath);
        ValidateRuntime(runtimePath);
        if (start.UseShellExecute || start.Arguments.Length != 0 || start.RedirectStandardInput || start.RedirectStandardOutput || start.RedirectStandardError)
            throw new ArgumentException("Use explicit ArgumentList/environment without shell or redirected streams.", nameof(start));
        using var reader = new PeFileReader(original);
        var pe = reader.Headers.PEHeader ?? throw new InvalidDataException("Missing child PE header.");
        if (pe.Magic != PEMagic.PE32Plus) throw new InvalidDataException("The launcher requires an x64 PE image.");
        string workingDirectory = Path.GetFullPath(string.IsNullOrEmpty(start.WorkingDirectory) ? Environment.CurrentDirectory : start.WorkingDirectory);
        string importName = LauncherLoadPlan.RelativeImportName(workingDirectory, runtimePath);
        var command = WindowsCommandLine.Build(start.FileName, start.ArgumentList);
        string environment = string.Join('\0', start.Environment.Where(pair => pair.Value is not null)
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).Select(pair => pair.Key + "=" + pair.Value)) + "\0\0";
        nint environmentBlock = Marshal.StringToHGlobalUni(environment);
        var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };
        ProcessInformation child = default;
        Process? ownedProcess = null;
        bool resumed = false;
        string stage = "create suspended child";
        try
        {
            uint flags = 0x00000004 | 0x00000400; // CREATE_SUSPENDED | CREATE_UNICODE_ENVIRONMENT
            if (start.CreateNoWindow) flags |= 0x08000000;
            if (!CreateProcessW(start.FileName, command, 0, 0, false, flags, environmentBlock,
                    workingDirectory, ref startup, out child))
            {
                int error = Marshal.GetLastWin32Error();
                child = default; // PROCESS_INFORMATION is undefined on failure; we own no handles.
                throw new Win32Exception(error);
            }
            stage = "locate verified EXE mapping";
            const int imageBaseField = 24; // PE32+ optional-header field.
            if (pe.SizeOfHeaders <= 0 || pe.SizeOfHeaders > 4 * 1024 * 1024)
                throw new InvalidDataException("Invalid child PE header size.");
            nint imageBase = RemoteProcessMemory.FindImage(child.Process, reader.Read(0, pe.SizeOfHeaders),
                reader.Headers.PEHeaderStartOffset + imageBaseField);
            var memory = new RemoteProcessMemory(child.Process, imageBase);
            stage = "reserve startup workspace";
            int arenaRva = memory.AllocateWorkspace(pe.SizeOfImage);
            stage = "validate startup workspace mapping";
            memory.ValidateWorkspace(StartupWorkspace.Create(arenaRva, pe.SizeOfImage));
            stage = "stage startup handoff";
            var plan = LauncherLoadPlan.Create(original, importName, arenaRva);
            memory.Write(arenaRva, plan.Imports);
            MemoryPatchTransaction.Apply(memory, plan.Edits);
            ownedProcess = Process.GetProcessById(child.ProcessId);
            _ = ownedProcess.Handle; // Acquire a stable handle before the child can exit.
            stage = "resume child";
            if (ResumeThread(child.Thread) != 1) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot resume the suspended child.");
            resumed = true;
            return ownedProcess;
        }
        catch (Exception error)
        {
            // Proton's debugstr channel captures this even when the Windows console
            // is not visible. Keep the original exception/native error for the UI.
            try { OutputDebugStringW($"[Dollhouse launcher] Startup failed at '{stage}': {error.GetType().Name}: {error.Message}"); }
            catch { /* Logging cannot interfere with suspended-child cleanup. */ }
            throw;
        }
        finally
        {
            if (!resumed && child.Process != 0)
            {
                TerminateProcess(child.Process, 1);
                WaitForSingleObject(child.Process, 5000);
                ownedProcess?.Dispose();
            }
            if (child.Thread != 0) CloseHandle(child.Thread);
            if (child.Process != 0) CloseHandle(child.Process);
            Marshal.FreeHGlobal(environmentBlock);
        }
    }

    private static void ValidateRuntime(string path)
    {
        using var stream = File.OpenRead(path);
        using var image = new PeFileReader(stream);
        var header = image.Headers.PEHeader;
        if (header?.Magic != PEMagic.PE32Plus || image.Headers.CoffHeader.Machine != Machine.Amd64
            || header.ExportTableDirectory.Size < 40)
            throw new InvalidDataException("The runtime is not an x64 DLL with an export table.");
        int exports = image.FileOffset(header.ExportTableDirectory.RelativeVirtualAddress);
        var directory = image.Read(exports, 40);
        int count = BinaryPrimitives.ReadInt32LittleEndian(directory.AsSpan(24));
        int names = image.FileOffset(BinaryPrimitives.ReadInt32LittleEndian(directory.AsSpan(32)));
        if (count < 0 || count > (stream.Length - names) / 4) throw new InvalidDataException("Invalid runtime export names.");
        var expected = Encoding.ASCII.GetBytes(LauncherLoadPlan.EntryExport + '\0');
        for (int index = 0; index < count; index++)
        {
            int offset = image.FileOffset(BinaryPrimitives.ReadInt32LittleEndian(image.Read(names + index * 4L, 4)));
            if (stream.Length - offset >= expected.Length && image.Read(offset, expected.Length).AsSpan().SequenceEqual(expected)) return;
        }
        throw new InvalidDataException($"Runtime export {LauncherLoadPlan.EntryExport} is missing. Run Dollhouse to restore its DLL.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int Size;
        public nint Reserved, Desktop, Title;
        public uint X, Y, XSize, YSize, XCount, YCount, Fill, Flags;
        public ushort ShowWindow, ReservedSize;
        public nint ReservedBytes, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation { public nint Process, Thread; public int ProcessId, ThreadId; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern bool CreateProcessW(string application, StringBuilder command, nint processAttributes, nint threadAttributes,
        bool inherit, uint flags, nint environment, string directory, ref StartupInfo startup, out ProcessInformation process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(nint thread);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(nint process, uint code);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(nint handle, uint timeout);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern void OutputDebugStringW(string text);
}
