namespace Dollhouse.Core.Patching.Native;

internal interface IPatchMemory
{
    byte[] Read(int rva, int length);
    void Write(int rva, byte[] bytes);
    uint Protect(int rva, int length, uint protection);
    void Flush();
}

/// <summary>Startup-only: the original game entry point has not run and no game threads execute these sites.</summary>
internal static class MemoryPatchTransaction
{
    internal static void Apply(IPatchMemory memory, IReadOnlyList<MemoryEdit> edits, Action<string>? log = null)
    {
        int end = 0;
        foreach (var edit in edits.OrderBy(edit => edit.Rva))
        {
            if (edit.Rva < end || edit.Expected.Length == 0 || edit.Expected.Length != edit.Replacement.Length)
                throw new InvalidDataException($"Invalid/overlapping memory patch: {edit.Name}.");
            end = checked(edit.Rva + edit.Expected.Length);
            if (!memory.Read(edit.Rva, edit.Expected.Length).AsSpan().SequenceEqual(edit.Expected))
                throw new InvalidDataException($"Unexpected loaded instructions/data: {edit.Name} (RVA 0x{edit.Rva:x}).");
        }
        var pages = edits.SelectMany(edit => Enumerable.Range(edit.Rva / 4096,
                (edit.Rva + edit.Expected.Length - 1) / 4096 - edit.Rva / 4096 + 1))
            .Distinct().Order().Select(page => page * 4096).ToArray();
        var protections = new List<(int Rva, uint Protection)>();
        var written = new List<MemoryEdit>();
        try
        {
            foreach (int page in pages) protections.Add((page, memory.Protect(page, 4096, 0x40)));
            foreach (var edit in edits)
            {
                written.Add(edit); // Include a possibly partially completed write in rollback.
                memory.Write(edit.Rva, edit.Replacement);
                if (!memory.Read(edit.Rva, edit.Replacement.Length).AsSpan().SequenceEqual(edit.Replacement))
                    throw new IOException($"Memory patch verification failed: {edit.Name}.");
            }
            memory.Flush();
        }
        catch
        {
            foreach (var edit in written.AsEnumerable().Reverse()) memory.Write(edit.Rva, edit.Expected);
            if (written.Count != 0) memory.Flush();
            throw;
        }
        finally
        {
            foreach (var page in protections.AsEnumerable().Reverse()) memory.Protect(page.Rva, 4096, page.Protection);
        }
        foreach (var edit in edits) log?.Invoke($"Applied {edit.Name} (RVA 0x{edit.Rva:x}).");
    }
}
