using System.Text;

namespace Dollhouse.Core.Patching.Native;

/// <summary>Serializes PE imports in format order, preserving existing descriptors verbatim.</summary>
internal sealed record ImportTable(byte[] Bytes, int DescriptorTableSize, IReadOnlyDictionary<ImportedFunction, int> Slots)
{
    public const int DescriptorSize = 20;
    private const int HintSize = sizeof(ushort);
    private const int ThunkSize = sizeof(long);

    public static ImportTable Build(string library, IReadOnlyList<ImportedFunction> functions, int baseAddress, byte[] existingDescriptors)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII);
        writer.Write(existingDescriptors);
        int newDescriptor = checked((int)stream.Position);
        Reserve(writer, DescriptorSize); // Descriptor for the added DLL, filled after addresses are known.
        Reserve(writer, DescriptorSize); // Null descriptor terminates the directory.
        int descriptorTableSize = checked((int)stream.Position);

        PadTo(writer, ThunkSize);
        int lookupTable = checked((int)stream.Position);
        Reserve(writer, ThunkSize * (functions.Count + 1));
        int addressTable = checked((int)stream.Position);
        Reserve(writer, ThunkSize * (functions.Count + 1));
        int libraryName = checked((int)stream.Position);
        WriteName(writer, library);

        var names = new Dictionary<ImportedFunction, int>();
        var slots = new Dictionary<ImportedFunction, int>();
        for (int i = 0; i < functions.Count; i++)
        {
            PadTo(writer, HintSize);
            names.Add(functions[i], checked(baseAddress + (int)stream.Position));
            slots.Add(functions[i], checked(baseAddress + addressTable + i * ThunkSize));
            writer.Write((ushort)0); // Import-by-name hint; lookup uses the full export name.
            WriteName(writer, functions[i].Name);
        }

        stream.Position = newDescriptor;
        writer.Write(baseAddress + lookupTable); // OriginalFirstThunk
        writer.Write(0);                         // TimeDateStamp (unbound)
        writer.Write(0);                         // ForwarderChain
        writer.Write(baseAddress + libraryName); // Name
        writer.Write(baseAddress + addressTable);// FirstThunk (IAT)
        foreach (int table in new[] { lookupTable, addressTable })
        {
            stream.Position = table;
            foreach (var function in functions) writer.Write((long)names[function]);
            // The reserved final entry stays zero, terminating the thunk table.
        }
        return new(stream.ToArray(), descriptorTableSize, slots);
    }

    private static void WriteName(BinaryWriter writer, string name)
    {
        writer.Write(Encoding.ASCII.GetBytes(name));
        writer.Write((byte)0);
    }
    private static void PadTo(BinaryWriter writer, int alignment) =>
        Reserve(writer, PeImage.Align(checked((int)writer.BaseStream.Position), alignment) - checked((int)writer.BaseStream.Position));
    private static void Reserve(BinaryWriter writer, int count) => writer.Write(new byte[count]);
}
