using System.Reflection.PortableExecutable;

namespace Dollhouse.Core.Patching.Native;

/// <summary>Seekable PE metadata/range reader. Never buffers the executable image.</summary>
internal sealed class PeFileReader : IDisposable
{
    private readonly Stream stream;
    private readonly PEReader reader;
    public PEHeaders Headers => reader.PEHeaders;

    public PeFileReader(Stream stream)
    {
        if (!stream.CanRead || !stream.CanSeek) throw new ArgumentException("PE input must be readable and seekable.");
        this.stream = stream;
        stream.Position = 0;
        reader = new PEReader(stream, PEStreamOptions.LeaveOpen);
    }

    public byte[] Read(long offset, int count)
    {
        if (offset < 0 || count < 0 || offset > stream.Length - count)
            throw new InvalidDataException("PE range is outside the file.");
        var bytes = new byte[count];
        stream.Position = offset;
        stream.ReadExactly(bytes);
        return bytes;
    }

    public int FileOffset(int rva)
    {
        foreach (var section in Headers.SectionHeaders)
            if (rva >= section.VirtualAddress && rva - section.VirtualAddress < section.SizeOfRawData)
                return checked(section.PointerToRawData + rva - section.VirtualAddress);
        throw new InvalidDataException("PE RVA is not backed by file bytes.");
    }

    public byte[] ReadImportDescriptors()
    {
        var directory = Headers.PEHeader!.ImportTableDirectory;
        int start = FileOffset(directory.RelativeVirtualAddress);
        // The launcher import arena is 64 KiB; a larger descriptor list cannot fit.
        using var result = new MemoryStream();
        for (int length = 0; length + ImportTable.DescriptorSize <= Math.Min(directory.Size, 65536); length += ImportTable.DescriptorSize)
        {
            var descriptor = Read(start + length, ImportTable.DescriptorSize);
            if (descriptor.AsSpan().IndexOfAnyExcept((byte)0) < 0) return result.ToArray();
            result.Write(descriptor);
        }
        throw new InvalidDataException("Unterminated or oversized PE import descriptors.");
    }

    public void Dispose() => reader.Dispose();
}
