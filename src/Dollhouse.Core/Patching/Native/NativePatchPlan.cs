namespace Dollhouse.Core.Patching.Native;

/// <summary>A fixed-build patch recipe. Each memory edit owns its original instruction validation and replacement span.</summary>
internal sealed partial class NativePatchPlan(int originalImageSize)
{
    private readonly List<ImportedFunction> imports = [];
    private readonly List<CodeEdit> edits = [];
    private readonly List<NativeLeaf> leaves = [];
    private readonly List<NativeData> data = [];
    public IReadOnlyList<string> ExportNames => imports.Select(function => function.Name).ToArray();

    public NativeData ReserveData(string name, int size, int alignment = 8)
    {
        if (size <= 0 || alignment <= 0 || alignment > 4096 || (alignment & (alignment - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(size));
        if (data.Any(block => block.Name == name)) throw new ArgumentException("Duplicate native data block.");
        var block = new NativeData(name, new byte[size], alignment);
        data.Add(block);
        return block;
    }

    public NativeLeaf Leaf(string name, Action<X64Code> implementation)
    {
        var leaf = new NativeLeaf(name, Sequence(implementation));
        leaves.Add(leaf);
        return leaf;
    }

    public ImportedFunction Import(string name)
    {
        if (imports.Any(function => function.Name == name)) throw new ArgumentException("Duplicate imported function.");
        var function = new ImportedFunction(name);
        imports.Add(function);
        return function;
    }

    public void RedirectFunction(string name, int entry, ImportedFunction replacement, Action<X64Code> expect)
    {
        var jump = new X64Code();
        jump.Jump(replacement);
        edits.Add(new(name, entry, null, Sequence(expect), jump));
    }

    public void RedirectCall(string name, int site, int originalTarget, ImportedFunction replacement)
    {
        var expected = new X64Code();
        expected.Call(originalTarget);
        var call = new X64Code();
        call.Call(replacement);
        edits.Add(new(name, site, null, expected, call));
    }

    // Leaf code must leave RSP and nonvolatile registers unchanged; it needs no unwind record.
    public void RedirectToLeaf(string name, int entry, Action<X64Code> expect, Action<X64Code> implementation)
    {
        var leaf = new NativeLeaf(name, Sequence(implementation));
        leaves.Add(leaf);
        var jump = new X64Code();
        jump.Jump(leaf);
        edits.Add(new(name, entry, null, Sequence(expect), jump));
    }

    public void ReplaceBlock(string name, int start, int end, Action<X64Code> expect, Action<X64Code> replacement) =>
        edits.Add(new(name, start, checked(end - start), Sequence(expect), Sequence(replacement)));

    private ImportedFunction[] CalledFunctions() => edits.SelectMany(edit => edit.Replacement.CalledFunctions).Distinct().ToArray();

    private (CodeEdit Edit, FileSpan Span)[] GetEditSpans(PeImage image) => edits.Select(edit =>
        (edit, new FileSpan(image.FileOffset(edit.Address), edit.Length ?? edit.Expected.Assemble(edit.Address).Length))).ToArray();

    private static void RequireSeparateEdits((CodeEdit Edit, FileSpan Span)[] spans)
    {
        int end = 0;
        foreach (var (edit, span) in spans.OrderBy(item => item.Span.Offset))
        {
            if (span.Length <= 0 || span.Offset < end)
                throw new InvalidDataException($"Invalid or overlapping patch span for '{edit.Name}'.");
            end = checked(span.Offset + span.Length);
        }
    }

    private static X64Code Sequence(Action<X64Code> define)
    {
        var code = new X64Code();
        define(code);
        return code;
    }

    private sealed record CodeEdit(string Name, int Address, int? Length, X64Code Expected, X64Code Replacement);
}

internal readonly record struct FileSpan(int Offset, int Length);
