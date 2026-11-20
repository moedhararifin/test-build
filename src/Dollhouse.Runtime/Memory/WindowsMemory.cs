using System.ComponentModel;
using System.Runtime.InteropServices;
using Dollhouse.Core.Patching.Native;
using Dollhouse.Core.Startup;

namespace Dollhouse.Runtime;

internal sealed class WindowsMemory(nint imageBase) : IPatchMemory
{
    public byte[] Read(int rva, int length)
    {
        var bytes = new byte[length];
        if (!ReadProcessMemory(GetCurrentProcess(), imageBase + rva, bytes, (nuint)length, out var count) || count != (nuint)length)
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Cannot read runtime memory (RVA=0x{rva:x}, bytes={length}, read={count}).");
        return bytes;
    }
    public void Write(int rva, byte[] bytes)
    {
        if (bytes.Length == 0) return;
        if (!WriteProcessMemory(GetCurrentProcess(), imageBase + rva, bytes, (nuint)bytes.Length, out var count) || count != (nuint)bytes.Length)
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Cannot write runtime memory (RVA=0x{rva:x}, bytes={bytes.Length}, written={count}).");
    }
    public uint Protect(int rva, int length, uint protection)
    {
        if (!VirtualProtect(imageBase + rva, (nuint)length, protection, out uint previous))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Cannot protect runtime memory (RVA=0x{rva:x}, bytes={length}, protection=0x{protection:x}).");
        return previous;
    }
    public void Flush()
    {
        if (!FlushInstructionCache(GetCurrentProcess(), 0, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot flush the runtime instruction cache.");
    }

    internal void ValidateWorkspace(StartupWorkspace workspace) => workspace.ValidateMapping(imageBase, address =>
    {
        if (VirtualQuery(address, out var region, (nuint)Marshal.SizeOf<StartupMemoryRegion>()) == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Cannot validate launcher workspace at 0x{address:x}.");
        return region;
    });
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool VirtualProtect(nint address, nuint size, uint protection, out uint old);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ReadProcessMemory(nint process, nint address, [Out] byte[] buffer, nuint size, out nuint read);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool WriteProcessMemory(nint process, nint address, byte[] buffer, nuint size, out nuint written);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool FlushInstructionCache(nint process, nint address, nuint size);
    [DllImport("kernel32.dll")] private static extern nint GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nuint VirtualQuery(nint address, out StartupMemoryRegion region, nuint length);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool VirtualFree(nint address, nuint size, uint type);

}
