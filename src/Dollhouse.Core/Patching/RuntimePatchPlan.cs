using System.Text;
using Dollhouse.Core.Patching.Native;
using Dollhouse.Core.Startup;

namespace Dollhouse.Core.Patching;

/// <summary>Connection and crypto changes to the loaded image; never changes the disk import table.</summary>
internal static class RuntimePatchPlan
{
    internal sealed record Selection(IReadOnlyList<MemoryEdit> Edits, MemoryPatchLayout? Gameplay);

    internal static Selection Build(byte[] original, RuntimeTrust trust, Func<string, nint> originalCrypto,
        Func<string, nint> runtime, int patchRva, bool noGameplayPatches)
    {
        var connection = ConnectionEdits(original, trust, originalCrypto, runtime);
        // Do not build, resolve exports for, or stage any gameplay hooks in this mode.
        // The temporary launcher handoff is still restored before either selection.
        if (noGameplayPatches) return new(connection, null);
        var gameplay = GameplayRuntimePatch.Plan.BuildMemory(original, patchRva, runtime);
        return new(connection.Concat(gameplay.Edits).ToArray(), gameplay);
    }

    internal static IReadOnlyList<MemoryEdit> ConnectionEdits(byte[] original, RuntimeTrust trust,
        Func<string, nint> originalCrypto, Func<string, nint> runtime)
    {
        using var image = new PeImage(original);
        var edits = new List<MemoryEdit>();
        Literal("Local HTTPS origin", GameBuild.UrlOffset, Encoding.Unicode,
            GameBuild.OldUrl + '\0', GameBuild.NewUrl + '\0');
        Literal("TLS certificate pin", GameBuild.PinOffset, Encoding.Unicode,
            GameBuild.OldPin + '\0', trust.CertificatePin + '\0');
        Literal("License verification root", GameBuild.LicenseRootOffset, Encoding.ASCII,
            GameBuild.OldLicenseRoot, trust.LicenseRoot);
        var imports = image.NamedImports("ncrypt.dll");
        if (imports.Count != 8) throw new InvalidDataException("Unexpected crypto import contract.");
        foreach (var import in imports)
        {
            nint expected = originalCrypto(import.Name), replacement = runtime(import.Name);
            if (expected == 0 || replacement == 0) throw new InvalidDataException($"Crypto export missing: {import.Name}.");
            edits.Add(new("Device crypto: " + import.Name, import.SlotRva,
                BitConverter.GetBytes((long)expected), BitConverter.GetBytes((long)replacement)));
        }
        return edits;

        void Literal(string name, int offset, Encoding encoding, string before, string after)
        {
            var expected = encoding.GetBytes(before);
            if (!original.AsSpan(offset, expected.Length).SequenceEqual(expected))
                throw new InvalidDataException($"Unexpected original data: {name}.");
            var replacement = new byte[expected.Length];
            encoding.GetBytes(after).CopyTo(replacement, 0);
            edits.Add(new(name, image.RvaForFileOffset(offset), expected, replacement));
        }
    }
}
