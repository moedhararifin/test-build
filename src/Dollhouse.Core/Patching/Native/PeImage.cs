using System.Buffers.Binary;
using System.Reflection.PortableExecutable;
using System.Text;

namespace Dollhouse.Core.Patching.Native;

/// <summary>PE layout only. This class knows neither game addresses nor gameplay hooks.</summary>
internal sealed class PeImage : IDisposable
{
    private readonly byte[] bytes;
    private readonly PEReader reader;
    private PEHeaders Headers => reader.PEHeaders;
    private PEHeader OptionalHeader => Headers.PEHeader ?? throw new InvalidDataException("Missing PE optional header.");

    public PeImage(byte[] bytes)
    {
        this.bytes = bytes;
        reader = new PEReader(new MemoryStream(bytes, writable: false));
        if (OptionalHeader.Magic != PEMagic.PE32Plus) throw new InvalidDataException("Expected an x64 PE image.");
    }

    public int FileOffset(int rva)
    {
        foreach (var section in Headers.SectionHeaders)
            if (rva >= section.VirtualAddress && rva - section.VirtualAddress < section.SizeOfRawData)
                return checked(section.PointerToRawData + rva - section.VirtualAddress);
        throw new InvalidDataException($"RVA 0x{rva:x} is not backed by file bytes.");
    }

    public int RvaForFileOffset(int offset)
    {
        foreach (var section in Headers.SectionHeaders)
            if (offset >= section.PointerToRawData && offset - section.PointerToRawData < section.SizeOfRawData)
                return checked(section.VirtualAddress + offset - section.PointerToRawData);
        throw new InvalidDataException("Patch data is outside the image sections.");
    }

    internal IReadOnlyList<(string Name, int SlotRva)> NamedImports(string library)
    {
        int descriptor = FileOffset(OptionalHeader.ImportTableDirectory.RelativeVirtualAddress);
        int end = checked(descriptor + OptionalHeader.ImportTableDirectory.Size);
        for (; descriptor + ImportTable.DescriptorSize <= end; descriptor += ImportTable.DescriptorSize)
        {
            int nameRva = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(descriptor + 12));
            if (nameRva == 0) break;
            if (!NameAt(FileOffset(nameRva)).Equals(library, StringComparison.OrdinalIgnoreCase)) continue;
            int lookup = FileOffset(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(descriptor)));
            int iat = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(descriptor + 16));
            var result = new List<(string, int)>();
            for (int index = 0; ; index++)
            {
                long thunk = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(lookup + index * 8));
                if (thunk == 0) return result;
                if (thunk < 0) throw new InvalidDataException("Unexpected ordinal import.");
                result.Add((NameAt(FileOffset(checked((int)thunk)) + 2), checked(iat + index * 8)));
            }
        }
        throw new InvalidDataException($"Import library not found: {library}.");

        string NameAt(int offset)
        {
            int terminator = Array.IndexOf(bytes, (byte)0, offset);
            if (terminator < 0) throw new InvalidDataException("Unterminated PE import name.");
            return Encoding.ASCII.GetString(bytes, offset, terminator - offset);
        }
    }

    internal byte[] ReadImportDescriptors()
    {
        var directory = OptionalHeader.ImportTableDirectory;
        int start = FileOffset(directory.RelativeVirtualAddress);
        int length = 0;
        while (length + ImportTable.DescriptorSize <= directory.Size)
        {
            if (bytes.AsSpan(start + length, ImportTable.DescriptorSize).IndexOfAnyExcept((byte)0) < 0)
                return bytes.AsSpan(start, length).ToArray();
            length += ImportTable.DescriptorSize;
        }
        throw new InvalidDataException("Unterminated PE import descriptors.");
    }

    internal static int Align(int value, int alignment) => checked((value + alignment - 1) / alignment * alignment);
    public void Dispose() => reader.Dispose();
}
