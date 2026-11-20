using System.Runtime.InteropServices;
using Dollhouse.Core.Patching.Native;

namespace Dollhouse.Core.Startup;

/// <summary>One launcher-owned reservation, made before the suspended child loads its DLLs.</summary>
internal readonly record struct StartupWorkspace(int BaseRva)
{
    internal const int Version = 1;
    internal const int PageSize = 4096;
    // Explicit budgets, checked against generated imports and patch layouts. Neither
    // partition may borrow the other: the OS loader retains its import references.
    internal const int ImportCapacity = 64 * 1024;
    internal const int PatchCapacity = 64 * 1024;
    internal const int TotalSize = ImportCapacity + PatchCapacity;
    internal int PatchRva => checked(BaseRva + ImportCapacity);

    internal static StartupWorkspace Create(int arenaRva, int imageSize)
    {
        if (arenaRva < imageSize || arenaRva % PageSize != 0 || arenaRva > int.MaxValue - TotalSize)
            throw new InvalidDataException("Startup workspace is outside the supported nearby address range or is not page aligned.");
        return new(arenaRva);
    }

    internal void ValidateMapping(nint imageBase, Func<nint, StartupMemoryRegion> query)
    {
        long start = checked(imageBase.ToInt64() + BaseRva);
        long patch = checked(imageBase.ToInt64() + PatchRva);
        long limit = checked(start + TotalSize);
        for (long position = start; position < limit;)
        {
            var region = query((nint)position);
            long end = checked(region.Base.ToInt64() + (long)region.Size);
            // Import pages may have been made read-only by the loader. Patch pages
            // must still be RW and all pages must belong to this same reservation.
            bool readable = region.Protection is 0x02 or 0x04;
            if (region.Base.ToInt64() > position || end <= position || end > limit ||
                region.AllocationBase.ToInt64() != start || region.State != 0x1000 || region.Type != 0x20000 ||
                !readable || (end > patch && region.Protection != 0x04))
                throw new InvalidDataException($"Invalid startup workspace mapping at 0x{position:x}: base=0x{region.Base:x}, allocation=0x{region.AllocationBase:x}, size=0x{region.Size:x}, state=0x{region.State:x}, protection=0x{region.Protection:x}, type=0x{region.Type:x}.");
            position = end;
        }
    }

    internal void StagePatch(IPatchMemory memory, MemoryPatchLayout layout)
    {
        int codeOffset = checked(layout.CodeRva - PatchRva);
        if (codeOffset < PageSize || codeOffset % PageSize != 0 || layout.Data.Length > codeOffset ||
            layout.Code.Length == 0 || (long)codeOffset + layout.Code.Length > PatchCapacity)
            throw new InvalidDataException($"Runtime patch layout exceeds its reserved partition: data={layout.Data.Length}, codeOffset={codeOffset}, code={layout.Code.Length}, capacity={PatchCapacity}.");
        memory.Write(PatchRva, layout.Data);
        memory.Write(layout.CodeRva, layout.Code);
        memory.Protect(layout.CodeRva, PatchCapacity - codeOffset, 0x20); // RX code; slots/descriptors and imports stay separate.
        memory.Flush();
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct StartupMemoryRegion
{
    internal nint Base, AllocationBase;
    internal uint AllocationProtection;
    internal ushort Partition, Padding;
    internal nuint Size;
    internal uint State, Protection, Type;
}
