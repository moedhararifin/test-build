using System.Buffers.Binary;

namespace Dollhouse.Core.Patching.Native;

internal sealed record MemoryEdit(string Name, int Rva, byte[] Expected, byte[] Replacement);
internal sealed record MemoryPatchLayout(byte[] Data, int CodeRva, byte[] Code, IReadOnlyList<MemoryEdit> Edits);

internal sealed partial class NativePatchPlan
{
    // Addresses remain relative to the game base, including the nearby allocation.
    // Absolute exported function pointers live in slots; all generated code is position independent.
    internal MemoryPatchLayout BuildMemory(byte[] original, int arenaRva, Func<string, nint> resolve)
    {
        if (original.Length != originalImageSize) throw new InvalidDataException("Unexpected original image size.");
        using var image = new PeImage(original);
        var spans = GetEditSpans(image);
        RequireSeparateEdits(spans);
        using var storage = new MemoryStream();
        var slots = new Dictionary<ImportedFunction, int>();
        Span<byte> encoded = stackalloc byte[8];
        foreach (var function in imports)
        {
            var pointer = resolve(function.Name);
            if (pointer == 0) throw new InvalidDataException($"Missing runtime export: {function.Name}.");
            slots.Add(function, checked(arenaRva + (int)storage.Position));
            BinaryPrimitives.WriteInt64LittleEndian(encoded, pointer);
            storage.Write(encoded);
        }
        var blocks = new Dictionary<NativeData, int>();
        foreach (var block in data)
        {
            int aligned = PeImage.Align(checked((int)storage.Position), block.Alignment);
            storage.Write(new byte[aligned - storage.Position]);
            blocks.Add(block, checked(arenaRva + aligned));
            storage.Write(block.Bytes);
        }
        int codeRva = checked(arenaRva + PeImage.Align(Math.Max(1, (int)storage.Length), 4096));
        using var code = new MemoryStream();
        var thunks = new Dictionary<ImportedFunction, int>();
        var leafAddresses = new Dictionary<NativeLeaf, int>();
        var addresses = new ImportAddresses(slots, thunks, leafAddresses, blocks);
        foreach (var function in CalledFunctions())
        {
            int rva = checked(codeRva + (int)code.Position);
            thunks.Add(function, rva);
            var thunk = new X64Code();
            thunk.Jump(function);
            code.Write(thunk.Assemble(rva, addresses));
        }
        foreach (var leaf in leaves)
        {
            int rva = checked(codeRva + (int)code.Position);
            leafAddresses.Add(leaf, rva);
            code.Write(leaf.Code.Assemble(rva, addresses));
        }
        var writes = new List<MemoryEdit>();
        foreach (var (edit, span) in spans)
        {
            var expected = original.AsSpan(span.Offset, span.Length).ToArray();
            var prefix = edit.Expected.Assemble(edit.Address);
            if (!expected.AsSpan().StartsWith(prefix))
                throw new InvalidDataException($"Unexpected original instructions for {edit.Name}.");
            X64Code.RequireWholeInstructions(expected, edit.Address);
            var replacement = edit.Replacement.Assemble(edit.Address, addresses);
            if (replacement.Length > span.Length) throw new InvalidDataException($"Oversized edit: {edit.Name}.");
            var padded = Enumerable.Repeat((byte)0x90, span.Length).ToArray();
            replacement.CopyTo(padded, 0);
            writes.Add(new(edit.Name, edit.Address, expected, padded));
        }
        return new(storage.ToArray(), codeRva, code.ToArray(), writes);
    }
}
