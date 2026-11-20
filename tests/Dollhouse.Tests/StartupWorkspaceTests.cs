using System.Buffers.Binary;
using System.ComponentModel;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using Dollhouse.Core.Patching.Native;
using Dollhouse.Core.Startup;

namespace Dollhouse.Tests;

internal static class StartupWorkspaceTests
{
    private const int Arena = 0x10000;
    private static readonly long ImageBase = 0x140000000;
    private const long Entry = 0x12345678;

    internal static void Verify()
    {
        Check.Equal(48, Marshal.SizeOf<StartupMemoryRegion>());
        Check.Equal(24, Marshal.OffsetOf<StartupMemoryRegion>(nameof(StartupMemoryRegion.Size)).ToInt32());
        Check.Equal(32, Marshal.OffsetOf<StartupMemoryRegion>(nameof(StartupMemoryRegion.State)).ToInt32());
        VerifyAllocation();
        VerifyMappings();
        VerifyHandoff();
    }

    private static StartupMemoryRegion Region(long start, long size, uint state = 0x1000, uint protection = 4) => new()
    {
        Base = (nint)start, AllocationBase = (nint)(ImageBase + Arena), Size = (nuint)size,
        State = state, Protection = protection, Type = 0x20000
    };

    private static void VerifyAllocation()
    {
        long first = ImageBase + 0x10000;
        int attempts = 0;
        int result = StartupWorkspaceAllocator.Allocate((nint)ImageBase, 0x3000, 0x10000,
            address => address == first ? Region(first, 0x10000, 0x2000) : Region(first + 0x10000, StartupWorkspace.TotalSize, 0x10000),
            (address, size) => { attempts++; Check.Equal(StartupWorkspace.TotalSize, size); return new(address, 0); });
        Check.Equal(0x20000, result); Check.Equal(1, attempts);

        var exhausted = Check.Throws<IOException>(() => StartupWorkspaceAllocator.Allocate((nint)ImageBase, 0x3000, 0x10000,
            _ => Region(first, int.MaxValue, 0x2000), (_, _) => throw new Exception("Reserved memory must not be allocated.")));
        Check.True(exhausted.Message.Contains("allocationAttempts=0") && exhausted.Message.Contains("bytes=131072"), "Missing allocation failure summary.");
        var query = Check.Throws<IOException>(() => StartupWorkspaceAllocator.Allocate((nint)ImageBase, 0x3000, 0x10000,
            _ => throw new Win32Exception(487), (_, _) => throw new Exception("Query failed.")));
        Check.True(query.Message.Contains("queryError=487") && query.InnerException is Win32Exception, "Query error was lost.");

        var denied = Check.Throws<IOException>(() => StartupWorkspaceAllocator.Allocate((nint)ImageBase, 0x3000, 0x10000,
            address => address.ToInt64() < first + StartupWorkspace.TotalSize
                ? Region(first, StartupWorkspace.TotalSize, 0x10000)
                : Region(first + StartupWorkspace.TotalSize, int.MaxValue, 0x2000),
            (_, _) => new(0, 5)));
        Check.True(denied.Message.Contains("allocationAttempts=1") && denied.Message.Contains("lastAllocationError=5"), "VirtualAllocEx error was lost.");
        Check.Throws<IOException>(() => StartupWorkspaceAllocator.Allocate((nint)ImageBase, 0x3000, 0x10000,
            _ => Region(first, 0), (_, _) => new(0, 0)));
        Check.Throws<IOException>(() => StartupWorkspaceAllocator.Allocate((nint)ImageBase, 0x3000, 0x10000,
            _ => Region(first, StartupWorkspace.TotalSize, 0x10000), (address, _) => new(address + 0x10000, 0)));
        Check.Throws<ArgumentOutOfRangeException>(() => StartupWorkspaceAllocator.Allocate((nint)ImageBase, 0x3000, 123,
            _ => default, (_, _) => default));
    }

    private static void VerifyMappings()
    {
        var workspace = StartupWorkspace.Create(Arena, 0x3000);
        long start = ImageBase + Arena;
        workspace.ValidateMapping((nint)ImageBase, _ => Region(start, StartupWorkspace.TotalSize));
        // Loader-owned imports may be read-only; the unused patch partition must be RW.
        workspace.ValidateMapping((nint)ImageBase, address => address == start
            ? Region(start, StartupWorkspace.ImportCapacity, protection: 2)
            : Region(start + StartupWorkspace.ImportCapacity, StartupWorkspace.PatchCapacity));
        foreach (string failure in new[] { "foreign", "reserved", "image", "guard", "readonly-patch", "oversized", "empty" })
        {
            var region = Region(start, StartupWorkspace.TotalSize);
            switch (failure)
            {
                case "foreign": region.AllocationBase += 0x10000; break;
                case "reserved": region.State = 0x2000; break;
                case "image": region.Type = 0x1000000; break;
                case "guard": region.Protection = 0x104; break;
                case "readonly-patch": region.Protection = 2; break;
                case "oversized": region.Size += 4096; break;
                case "empty": region.Size = 0; break;
            }
            Check.Throws<InvalidDataException>(() => workspace.ValidateMapping((nint)ImageBase, _ => region));
        }
        Check.Throws<InvalidDataException>(() => StartupWorkspace.Create(0x1000, 0x3000));
        Check.Throws<InvalidDataException>(() => StartupWorkspace.Create(Arena + 1, 0x3000));
        Check.Throws<InvalidDataException>(() => StartupWorkspace.Create(int.MaxValue - 4095, 0x3000));
    }

    private static void VerifyHandoff()
    {
        var original = RuntimeLoaderFixture.Executable();
        using var pe = new PEReader(new MemoryStream(original, false));
        int header = pe.PEHeaders.PEHeaderStartOffset + 120;
        const string runtime = @".\.dollhouse\runtime\dhruntime.dll";
        Check.Equal(runtime, LauncherLoadPlan.RelativeImportName(@"C:\游戏 日本語 😀", @"C:\游戏 日本語 😀\.dollhouse\runtime\dhruntime.dll"));
        Check.Throws<ArgumentException>(() => LauncherLoadPlan.RelativeImportName(@"C:\fixture", @"D:\runtime.dll"));
        foreach (string invalid in new[] { "", @"C:\runtime.dll", @"\runtime.dll", "runtime\0.dll", "运行.dll" })
            Check.Throws<ArgumentException>(() => LauncherLoadPlan.Create(original, invalid, Arena));
        var plan = LauncherLoadPlan.Create(original, runtime, Arena);
        var workspace = StartupWorkspace.Create(Arena, pe.PEHeaders.PEHeader!.SizeOfImage);
        var memory = Loaded(original, plan);
        int validations = 0;
        var restored = LauncherLoadPlan.Restore(original, runtime, (nint)Entry, memory,
            value => { Check.Equal(workspace, value); validations++; });
        Check.Equal(1, validations); Check.Equal(workspace, restored);
        foreach (var edit in plan.Edits) Check.True(memory.Read(edit.Rva, edit.Expected.Length).AsSpan().SequenceEqual(edit.Expected), "Startup was not restored.");

        var imports = memory.Read(Arena, StartupWorkspace.ImportCapacity);
        int code = workspace.PatchRva + StartupWorkspace.PageSize;
        workspace.StagePatch(memory, new([1, 2, 3], code, [0xc3], []));
        Check.True(memory.Read(Arena, imports.Length).AsSpan().SequenceEqual(imports), "Patch staging overwrote loader imports.");
        Check.Equal((workspace.PatchRva + 4096, StartupWorkspace.PatchCapacity - 4096, 0x20U), memory.Protections.Last());
        foreach (var layout in new[] {
            new MemoryPatchLayout([], Arena, [0xc3], []),
            new MemoryPatchLayout(new byte[4097], code, [0xc3], []),
            new MemoryPatchLayout([], code + 1, [0xc3], []),
            new MemoryPatchLayout([], workspace.PatchRva + StartupWorkspace.PatchCapacity, [0xc3], []) })
        {
            int writes = memory.Writes;
            Check.Throws<InvalidDataException>(() => workspace.StagePatch(memory, layout));
            Check.Equal(writes, memory.Writes);
        }

        // Every byte of the payload is deterministic except the OS-resolved entry
        // slot. Include descriptors, DLL/export names, terminators and version/budgets.
        foreach (int offset in new[] { 0, plan.Imports.Length - 1, plan.Imports.Length - 4,
            plan.Imports.Length - 8, plan.Imports.Length - 12, plan.Imports.Length - 16, plan.EntrySlotRva - Arena })
        {
            var bad = Loaded(original, plan); bad.Bytes[Arena + offset] ^= 1;
            int writes = bad.Writes;
            Check.Throws<InvalidDataException>(() => LauncherLoadPlan.Restore(original, runtime, (nint)Entry, bad, _ => { }));
            Check.Equal(writes, bad.Writes); // Reject before restoring headers/entry or staging code.
        }
        foreach (int field in new[] { header, header + 4 })
        {
            var bad = Loaded(original, plan); BinaryPrimitives.WriteInt32LittleEndian(bad.Bytes.AsSpan(field), 0);
            Check.Throws<InvalidDataException>(() => LauncherLoadPlan.Restore(original, runtime, (nint)Entry, bad, _ => { }));
        }
        var rejected = Loaded(original, plan);
        int before = rejected.Writes;
        Check.Throws<InvalidDataException>(() => LauncherLoadPlan.Restore(original, runtime, (nint)Entry, rejected,
            _ => throw new InvalidDataException("Foreign allocation.")));
        Check.Equal(before, rejected.Writes);
    }

    private static Memory Loaded(byte[] original, LauncherLoadPlan plan)
    {
        var memory = new Memory();
        using var pe = new PEReader(new MemoryStream(original, false));
        original.AsSpan(0, pe.PEHeaders.PEHeader!.SizeOfHeaders).CopyTo(memory.Bytes);
        foreach (var section in pe.PEHeaders.SectionHeaders)
            original.AsSpan(section.PointerToRawData, section.SizeOfRawData).CopyTo(memory.Bytes.AsSpan(section.VirtualAddress));
        memory.Write(Arena, plan.Imports);
        MemoryPatchTransaction.Apply(memory, plan.Edits);
        BinaryPrimitives.WriteInt64LittleEndian(memory.Bytes.AsSpan(plan.EntrySlotRva, 8), Entry);
        return memory;
    }

    private sealed class Memory : IPatchMemory
    {
        internal readonly byte[] Bytes = new byte[Arena + StartupWorkspace.TotalSize];
        internal readonly List<(int, int, uint)> Protections = [];
        internal int Writes;
        public byte[] Read(int rva, int length) => Bytes.AsSpan(rva, length).ToArray();
        public void Write(int rva, byte[] bytes) { Writes++; bytes.CopyTo(Bytes, rva); }
        public uint Protect(int rva, int length, uint protection) { Protections.Add((rva, length, protection)); return 4; }
        public void Flush() { }
    }
}
