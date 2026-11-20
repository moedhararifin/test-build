using System.Buffers.Binary;
using System.Reflection.PortableExecutable;
using Dollhouse.Core.Patching.Native;
using Iced.Intel;

namespace Dollhouse.Core.Startup;

/// <summary>Temporary import and entry redirect for a suspended x64 process. Never writes the EXE file.</summary>
internal sealed record LauncherLoadPlan(int ArenaRva, byte[] Imports, int EntrySlotRva, IReadOnlyList<MemoryEdit> Edits)
{
    internal const string EntryExport = "DollhouseMain";
    private const int HandoffSize = 4 * sizeof(int);
    private const int ImportDirectory = 120, BoundDirectory = 200, IatDirectory = 208;

    internal static string RelativeImportName(string workingDirectory, string libraryPath)
    {
        // Production's relative components are fixed ASCII (.dollhouse/runtime),
        // while CreateProcessW supplies the Unicode installation root separately.
        string relative = Path.GetRelativePath(Path.GetFullPath(workingDirectory), Path.GetFullPath(libraryPath));
        string importName = ".\\" + relative.Replace('/', '\\');
        ValidateImportName(importName);
        return importName;
    }

    private static void ValidateImportName(string importName)
    {
        if (string.IsNullOrEmpty(importName) || importName.Any(character => character > 127 || character == '\0')
            || Path.IsPathRooted(importName) || importName.Contains(':'))
            throw new ArgumentException("The launcher import must use a relative ASCII DLL path.", nameof(importName));
    }

    internal static LauncherLoadPlan Create(byte[] original, string importName, int arenaRva)
    {
        using var stream = new MemoryStream(original, false);
        return Create(stream, importName, arenaRva);
    }

    internal static LauncherLoadPlan Create(Stream original, string importName, int arenaRva)
    {
        ValidateImportName(importName);
        using var image = new PeFileReader(original);
        var headers = image.Headers;
        var pe = headers.PEHeader ?? throw new InvalidDataException("Missing PE header.");
        if (pe.Magic != PEMagic.PE32Plus || headers.CoffHeader.Machine != Machine.Amd64)
            throw new InvalidDataException("Launcher loading requires an x64 image and a nearby allocation.");
        _ = StartupWorkspace.Create(arenaRva, pe.SizeOfImage);
        // Only the controlled relative path enters the narrow import table, never
        // the Unicode absolute installation path. Windows/Wine resolve it normally.
        var entry = new ImportedFunction(EntryExport);
        var imports = ImportTable.Build(importName, [entry], arenaRva, image.ReadImportDescriptors());
        var payload = new byte[checked(imports.Bytes.Length + HandoffSize)];
        imports.Bytes.CopyTo(payload, 0);
        var handoff = payload.AsSpan(imports.Bytes.Length);
        BinaryPrimitives.WriteInt32LittleEndian(handoff, StartupWorkspace.Version);
        BinaryPrimitives.WriteInt32LittleEndian(handoff[4..], StartupWorkspace.TotalSize);
        BinaryPrimitives.WriteInt32LittleEndian(handoff[8..], StartupWorkspace.ImportCapacity);
        BinaryPrimitives.WriteInt32LittleEndian(handoff[12..], StartupWorkspace.PatchCapacity);
        if (payload.Length > StartupWorkspace.ImportCapacity) throw new InvalidDataException("Launcher import/handoff payload exceeds its reserved partition.");
        var edits = new List<MemoryEdit>();
        // The directory lives outside the mapped image: describe the entire
        // allocation payload, including names/thunks, for loader range checks.
        Directory("Temporary runtime import", ImportDirectory, arenaRva, payload.Length);
        Directory("Disable bound imports during startup", BoundDirectory, 0, 0);
        if (pe.ImportAddressTableDirectory.RelativeVirtualAddress == 0)
        {
            // With no explicit IAT directory the loader searches the import's
            // section, which cannot cover our separate allocation. Keep its
            // protection bookkeeping anchored to the original import section.
            var section = headers.SectionHeaders.Single(section => pe.ImportTableDirectory.RelativeVirtualAddress >= section.VirtualAddress
                && pe.ImportTableDirectory.RelativeVirtualAddress < section.VirtualAddress + section.SizeOfRawData);
            Directory("Original IAT protection range", IatDirectory, section.VirtualAddress, section.SizeOfRawData);
        }
        int offset = image.FileOffset(pe.AddressOfEntryPoint);
        var entryBytes = image.Read(offset, 64);
        var decoder = Iced.Intel.Decoder.Create(64, new ByteArrayCodeReader(entryBytes), (ulong)pe.AddressOfEntryPoint);
        int length = 0;
        while (length < 6)
        {
            var instruction = decoder.Decode();
            if (instruction.IsInvalid) throw new InvalidDataException("Invalid original entry instructions.");
            length += instruction.Length;
        }
        var jump = new X64Code();
        jump.Jump(entry);
        var replacement = Enumerable.Repeat((byte)0x90, length).ToArray();
        jump.Assemble(pe.AddressOfEntryPoint, new(imports.Slots, new Dictionary<ImportedFunction, int>())).CopyTo(replacement, 0);
        edits.Add(new("Compiled runtime entry handoff", pe.AddressOfEntryPoint, entryBytes.AsSpan(0, length).ToArray(), replacement));
        return new(arenaRva, payload, imports.Slots[entry], edits);

        void Directory(string name, int field, int rva, int size)
        {
            int position = headers.PEHeaderStartOffset + field;
            byte[] value = new byte[8];
            BinaryPrimitives.WriteInt32LittleEndian(value, rva);
            BinaryPrimitives.WriteInt32LittleEndian(value.AsSpan(4), size);
            var expected = image.Read(position, 8);
            if (!expected.AsSpan().SequenceEqual(value))
                edits.Add(new(name, position, expected, value));
        }
    }

    internal static StartupWorkspace Restore(byte[] original, string importName, nint expectedEntry,
        IPatchMemory memory, Action<StartupWorkspace> validateMapping)
    {
        using var reader = new PEReader(new MemoryStream(original, false));
        int position = reader.PEHeaders.PEHeaderStartOffset + ImportDirectory;
        var directory = memory.Read(position, 8);
        int arena = BinaryPrimitives.ReadInt32LittleEndian(directory);
        int size = BinaryPrimitives.ReadInt32LittleEndian(directory.AsSpan(4));
        var workspace = StartupWorkspace.Create(arena, reader.PEHeaders.PEHeader!.SizeOfImage);
        var plan = Create(original, importName, arena);
        if (size != plan.Imports.Length) throw new InvalidDataException("Invalid launcher import/handoff size or mismatched launcher/runtime version.");
        validateMapping(workspace); // Verify reservation ownership/protections before reading its payload.
        var storage = memory.Read(arena, size);
        int slot = plan.EntrySlotRva - arena;
        if (expectedEntry == 0 || BinaryPrimitives.ReadInt64LittleEndian(storage.AsSpan(slot, 8)) != expectedEntry)
            throw new InvalidDataException("The loader did not resolve the expected runtime entry.");
        var expected = (byte[])plan.Imports.Clone();
        BinaryPrimitives.WriteInt64LittleEndian(expected.AsSpan(slot, 8), expectedEntry);
        if (!storage.AsSpan().SequenceEqual(expected))
            throw new InvalidDataException("Launcher import/handoff contents differ from the validated plan (DLL, export, version or workspace layout).");
        MemoryPatchTransaction.Apply(memory, plan.Edits.Select(edit => new MemoryEdit(edit.Name, edit.Rva, edit.Replacement, edit.Expected)).ToArray());
        // Retain the resolved import storage until process exit. The OS loader
        // may retain references to it even after the original headers are restored.
        return workspace;
    }
}
