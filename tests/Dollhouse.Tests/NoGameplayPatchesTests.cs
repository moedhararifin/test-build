using System.Buffers.Binary;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using Dollhouse.Core.Patching;
using Dollhouse.Core.Patching.Native;
using Dollhouse.Core.Startup;

namespace Dollhouse.Tests;

internal static class NoGameplayPatchesTests
{
    internal static void Verify()
    {
        Check.True(!RuntimeLaunchOptions.ParseNoGameplayPatches(null), "Absent flag must enable gameplay.");
        Check.True(!RuntimeLaunchOptions.ParseNoGameplayPatches("0"), "Explicit default was lost.");
        Check.True(RuntimeLaunchOptions.ParseNoGameplayPatches("1"), "Disabled mode was lost.");
        foreach (string invalid in new[] { "", "true", "false", " 1", "2" })
            Check.Throws<InvalidDataException>(() => RuntimeLaunchOptions.ParseNoGameplayPatches(invalid));
        if (GameBinaryFixture.Original is not { } fixture)
        { Console.WriteLine("SKIP licensing-only memory checks: no original game fixture."); return; }
        byte[] original = File.ReadAllBytes(fixture);
        using var image = new PeImage(original);
        var crypto = image.NamedImports("ncrypt.dll").Select(import => import.Name).ToHashSet();
        int resolved = 0;
        nint LicensingExport(string name)
        {
            Check.True(crypto.Contains(name), "Disabled mode resolved a gameplay export: " + name);
            resolved++;
            return 0x2000;
        }
        var trust = new RuntimeTrust(new string('a', 64), GameBuild.OldLicenseRoot);
        // An unusable gameplay arena proves this branch never builds a gameplay layout.
        var disabled = RuntimePatchPlan.Build(original, trust, _ => 0x1000, LicensingExport, -1, true);
        Check.True(disabled.Gameplay == null, "Disabled mode produced gameplay code/data.");
        Check.Equal(8, resolved); Check.Equal(11, disabled.Edits.Count);
        var allowed = image.NamedImports("ncrypt.dll").Select(import => (import.SlotRva, Size: 8))
            .Concat(new[]
            {
                (image.RvaForFileOffset(GameBuild.UrlOffset), Encoding.Unicode.GetByteCount(GameBuild.OldUrl + '\0')),
                (image.RvaForFileOffset(GameBuild.PinOffset), Encoding.Unicode.GetByteCount(GameBuild.OldPin + '\0')),
                (image.RvaForFileOffset(GameBuild.LicenseRootOffset), Encoding.ASCII.GetByteCount(GameBuild.OldLicenseRoot))
            }).ToHashSet();
        Check.True(disabled.Edits.Select(edit => (edit.Rva, edit.Replacement.Length)).ToHashSet().SetEquals(allowed),
            "Licensing-only writes differ from the exact connection/crypto allowlist.");

        const int arena = 0x60000000;
        var normal = RuntimePatchPlan.Build(original, trust, _ => 0x1000, _ => 0x2000, arena, false);
        var current = GameplayRuntimePatch.Plan.BuildMemory(original, arena, _ => 0x2000);
        Check.True(normal.Gameplay != null && normal.Gameplay.Code.AsSpan().SequenceEqual(current.Code) &&
            normal.Gameplay.Data.AsSpan().SequenceEqual(current.Data), "Default gameplay layout changed.");
        Check.Equal(disabled.Edits.Count + current.Edits.Count, normal.Edits.Count);
        for (int i = 0; i < current.Edits.Count; i++)
        {
            var actual = normal.Edits[disabled.Edits.Count + i]; var expected = current.Edits[i];
            Check.Equal(expected.Rva, actual.Rva);
            Check.True(expected.Expected.AsSpan().SequenceEqual(actual.Expected) &&
                expected.Replacement.AsSpan().SequenceEqual(actual.Replacement), "Default gameplay edit changed.");
        }
        VerifyWrites(original, disabled.Edits, []);
        string? sample = Environment.GetEnvironmentVariable("DOLLHOUSE_TEST_STATS_PATCH");
        if (string.IsNullOrWhiteSpace(sample))
            Console.WriteLine("SKIP external StatsPatch sample: set DOLLHOUSE_TEST_STATS_PATCH to its version.dll.");
        else VerifyStatsPatch(original, disabled.Edits, sample);
    }

    private static void VerifyStatsPatch(byte[] original, IReadOnlyList<MemoryEdit> licensing, string sample)
    {
        byte[] dll = File.ReadAllBytes(sample);
        Check.Equal("20e1808831d9bf3d61b84ecac4ffc9778123393e26ed8650abba327f7b500acd",
            Convert.ToHexStringLower(SHA256.HashData(dll)));
        using var stats = new PeImage(dll);
        using var reader = new PEReader(new MemoryStream(dll, false));
        ulong imageBase = reader.PEHeaders.PEHeader!.ImageBase;
        var patches = new List<MemoryEdit>();
        for (int i = 0; i < 12; i++)
        {
            // Fixed table is used only after matching this exact external DLL hash.
            int entry = stats.FileOffset(0xf070 + i * 24);
            int rva = BinaryPrimitives.ReadInt32LittleEndian(dll.AsSpan(entry));
            int size = BinaryPrimitives.ReadInt32LittleEndian(dll.AsSpan(entry + 4));
            int before = stats.FileOffset(checked((int)(BinaryPrimitives.ReadUInt64LittleEndian(dll.AsSpan(entry + 8)) - imageBase)));
            int after = stats.FileOffset(checked((int)(BinaryPrimitives.ReadUInt64LittleEndian(dll.AsSpan(entry + 16)) - imageBase)));
            patches.Add(new("StatsPatch " + i, rva, dll.AsSpan(before, size).ToArray(), dll.AsSpan(after, size).ToArray()));
        }
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(original.AsSpan(0, 0x9667000));
        hash.AppendData(new byte[0x2000]);
        hash.AppendData(original.AsSpan(0x9669000));
        Check.Equal("1ea269a0e94d354641f6b8cc1fd9bc58f99f4eebba350c4d2da9cf89cbc32f64",
            Convert.ToHexStringLower(hash.GetHashAndReset())); // Installer's masked supported-image hash.
        using var game = new PeImage(original);
        var requiredExports = game.NamedImports("version.dll").Select(import => import.Name);
        int exports = stats.FileOffset(reader.PEHeaders.PEHeader.ExportTableDirectory.RelativeVirtualAddress);
        int count = BinaryPrimitives.ReadInt32LittleEndian(dll.AsSpan(exports + 24));
        int names = stats.FileOffset(BinaryPrimitives.ReadInt32LittleEndian(dll.AsSpan(exports + 32)));
        var available = new HashSet<string>();
        for (int i = 0; i < count; i++)
        {
            int name = stats.FileOffset(BinaryPrimitives.ReadInt32LittleEndian(dll.AsSpan(names + i * 4)));
            available.Add(Encoding.ASCII.GetString(dll, name, Array.IndexOf(dll, (byte)0, name) - name));
        }
        Check.True(requiredExports.All(available.Contains), "StatsPatch proxy lacks a required version.dll export.");
        VerifyWrites(original, licensing, patches); // Capacity=0, native per-character capacity.
        patches.Add(new("StatsPatch Capacity=2000", 0x549b7d8,
            dll.AsSpan(stats.FileOffset(0xe320), 5).ToArray(), [0xb8, 0xd0, 7, 0, 0]));
        VerifyWrites(original, licensing, patches);
        Console.WriteLine("PASS StatsPatch 2.4 static coexistence: installer hash, proxy exports, 12 preimages, both capacity modes; no external code executed.");
    }

    private static void VerifyWrites(byte[] original, IReadOnlyList<MemoryEdit> licensing, IReadOnlyList<MemoryEdit> external)
    {
        using var image = new PeImage(original);
        using var reader = new PEReader(new MemoryStream(original, false));
        int entry = reader.PEHeaders.PEHeader!.AddressOfEntryPoint;
        var memory = new SimulatedImage(original, image);
        foreach (var edit in external)
        {
            Check.True(memory.Read(edit.Rva, edit.Expected.Length).AsSpan().SequenceEqual(edit.Expected), "External patch preimage differs from original.");
            Check.True(entry < edit.Rva || entry >= edit.Rva + edit.Replacement.Length, "External patch touches the temporary launcher entry.");
            foreach (var own in licensing)
                Check.True(edit.Rva >= own.Rva + own.Replacement.Length || own.Rva >= edit.Rva + edit.Replacement.Length,
                    "External patch overlaps licensing: " + own.Name);
            memory.Seed(edit.Rva, edit.Replacement); // Model DllMain writes before DollhouseMain; never execute them.
        }
        foreach (var edit in licensing.Where(edit => edit.Name.StartsWith("Device crypto: ", StringComparison.Ordinal)))
            memory.Seed(edit.Rva, edit.Expected); // Model the OS-resolved IAT, not its disk name RVAs.
        MemoryPatchTransaction.Apply(memory, licensing);
        Check.Equal(11, memory.Writes.Count);
        Check.True(memory.Writes.SequenceEqual(licensing.Select(edit => (edit.Rva, edit.Replacement.Length))), "Unexpected runtime write.");
        foreach (var edit in external)
            Check.True(memory.Read(edit.Rva, edit.Replacement.Length).AsSpan().SequenceEqual(edit.Replacement), "Dollhouse overwrote external patch bytes.");
    }

    private sealed class SimulatedImage(byte[] original, PeImage image) : IPatchMemory
    {
        private readonly Dictionary<int, byte> changes = [];
        internal readonly List<(int Rva, int Size)> Writes = [];
        internal void Seed(int rva, byte[] bytes)
        { for (int i = 0; i < bytes.Length; i++) changes[rva + i] = bytes[i]; }
        public byte[] Read(int rva, int length)
        {
            byte[] bytes = original.AsSpan(image.FileOffset(rva), length).ToArray();
            for (int i = 0; i < length; i++) if (changes.TryGetValue(rva + i, out byte value)) bytes[i] = value;
            return bytes;
        }
        public void Write(int rva, byte[] bytes) { Writes.Add((rva, bytes.Length)); Seed(rva, bytes); }
        public uint Protect(int rva, int length, uint protection) => 0x20;
        public void Flush() { }
    }
}
