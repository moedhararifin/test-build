using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Dollhouse.Core.Patching.Native;

namespace Dollhouse.Core.Startup;

/// <summary>Memory operations on the single suspended child owned by the launcher.</summary>
internal sealed class RemoteProcessMemory(nint process, nint imageBase) : IPatchMemory
{
    public byte[] Read(int rva, int length) => ReadAt(process, imageBase + rva, length);
    public void Write(int rva, byte[] bytes)
    {
        if (!WriteProcessMemory(process, imageBase + rva, bytes, (nuint)bytes.Length, out var count) || count != (nuint)bytes.Length)
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Cannot write suspended-child memory (RVA=0x{rva:x}, bytes={bytes.Length}, written={count}).");
    }
    public uint Protect(int rva, int length, uint protection)
    {
        if (!VirtualProtectEx(process, imageBase + rva, (nuint)length, protection, out uint previous))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Cannot protect suspended-child memory (RVA=0x{rva:x}, bytes={length}, protection=0x{protection:x}).");
        return previous;
    }
    public void Flush()
    {
        if (!FlushInstructionCache(process, 0, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot flush the suspended-child instruction cache.");
    }

    internal static nint FindImage(nint process, byte[] expectedHeaders, int imageBaseField)
    {
        var hints = new List<string>();
        int regions = 0;
        for (long address = 0; Query(process, address, out var region);)
        {
            regions++;
            if (region.Type == 0x1000000 && region.Base == region.AllocationBase && region.State == 0x1000)
            {
                try
                {
                    var actual = ReadAt(process, region.Base, expectedHeaders.Length);
                    if (actual.AsSpan().SequenceEqual(expectedHeaders)) return region.Base;
                    // Windows rebases this PE32+ field before returning from
                    // CreateProcess. Wine may retain the preferred image base.
                    var rebased = (byte[])expectedHeaders.Clone();
                    BinaryPrimitives.WriteInt64LittleEndian(rebased.AsSpan(imageBaseField), region.Base);
                    if (actual.AsSpan().SequenceEqual(rebased)) return region.Base;
                    int difference = Enumerable.Range(0, actual.Length).First(index => actual[index] != expectedHeaders[index]);
                    if (hints.Count < 8) hints.Add($"image 0x{region.Base:x}, first header difference 0x{difference:x}: {actual[difference]:x2}/{expectedHeaders[difference]:x2}");
                }
                catch (Win32Exception) { /* Other image mappings need not have readable headers. */ }
            }
            long next = checked(region.Base.ToInt64() + (long)region.Size);
            if (next <= address) break;
            address = next;
        }
        throw new InvalidDataException($"Cannot locate the verified EXE mapping in the suspended child ({regions} regions, Win32 {Marshal.GetLastWin32Error()}; {string.Join("; ", hints)}).");
    }

    internal int AllocateWorkspace(int imageSize)
    {
        GetSystemInfo(out var system);
        if (system.PageSize != StartupWorkspace.PageSize)
            throw new PlatformNotSupportedException($"Startup patch recipes require {StartupWorkspace.PageSize}-byte pages; OS reports {system.PageSize}.");
        return StartupWorkspaceAllocator.Allocate(imageBase, imageSize, checked((int)system.AllocationGranularity),
            QueryRequired, (address, size) =>
            {
                nint result = VirtualAllocEx(process, address, (nuint)size, 0x3000, 0x04);
                int error = result == 0 ? Marshal.GetLastWin32Error() : 0;
                return new(result, error);
            });
    }

    internal void ValidateWorkspace(StartupWorkspace workspace) => workspace.ValidateMapping(imageBase, QueryRequired);

    private StartupMemoryRegion QueryRequired(nint address)
    {
        if (!Query(process, address, out var region))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Cannot query suspended-child memory at 0x{address:x}.");
        return region;
    }
    private static byte[] ReadAt(nint process, nint address, int length)
    {
        var bytes = new byte[length];
        if (!ReadProcessMemory(process, address, bytes, (nuint)length, out var count) || count != (nuint)length)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot read suspended child memory.");
        return bytes;
    }
    private static bool Query(nint process, long address, out StartupMemoryRegion region) =>
        VirtualQueryEx(process, (nint)address, out region, (nuint)Marshal.SizeOf<StartupMemoryRegion>()) != 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemInfo
    {
        public uint Architecture, PageSize;
        public nint MinimumAddress, MaximumAddress;
        public nuint ActiveProcessorMask;
        public uint ProcessorCount, ProcessorType, AllocationGranularity;
        public ushort ProcessorLevel, ProcessorRevision;
    }
    [DllImport("kernel32.dll")] private static extern void GetSystemInfo(out SystemInfo system);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ReadProcessMemory(nint process, nint address, [Out] byte[] buffer, nuint size, out nuint read);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool WriteProcessMemory(nint process, nint address, byte[] buffer, nuint size, out nuint written);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool VirtualProtectEx(nint process, nint address, nuint size, uint protection, out uint previous);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool FlushInstructionCache(nint process, nint address, nuint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint VirtualAllocEx(nint process, nint address, nuint size, uint allocation, uint protection);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nuint VirtualQueryEx(nint process, nint address, out StartupMemoryRegion region, nuint size);
}
