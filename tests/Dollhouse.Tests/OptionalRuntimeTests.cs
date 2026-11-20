extern alias runtime;

using System.Diagnostics;
using System.Buffers.Binary;
using System.Reflection.PortableExecutable;
using System.Text;
using Dollhouse.Core.Patching;
using Dollhouse.Core.Patching.Native;
using Dollhouse.Core.Startup;

namespace Dollhouse.Tests;

internal static class OptionalRuntimeTests
{
    internal static void Verify()
    {
        foreach (var name in new[] { "aphrodisiac-effects", "simultaneous-climax", "cumflation" })
            Check.Throws<ArgumentException>(() => Dollhouse.App.AppOptions.Parse(["--game-patch", name]));
        VerifyTransaction();
        VerifyWindowsMemory();
        LauncherLoadingTests.Verify();
        VerifyCurrentPlan();
        VerifyExports();
    }

    private static unsafe void VerifyWindowsMemory()
    {
        if (!OperatingSystem.IsWindows()) return;
        // Use an address of our own module solely as the allocation anchor.
        var anchor = System.Runtime.InteropServices.NativeLibrary.Load("kernel32.dll");
        using var process = Process.GetCurrentProcess();
        var remote = new RemoteProcessMemory(process.Handle, anchor);
        int arena = remote.AllocateWorkspace(0x200000);
        var workspace = StartupWorkspace.Create(arena, 0x200000);
        remote.ValidateWorkspace(workspace);
        var allocation = anchor + arena;
        try
        {
            var memory = new runtime::Dollhouse.Runtime.WindowsMemory(anchor);
            memory.ValidateWorkspace(workspace);
            int rva = workspace.PatchRva + StartupWorkspace.PageSize;
            byte[] return42 = [0xb8, 42, 0, 0, 0, 0xc3];
            workspace.StagePatch(memory, new([1, 2, 3, 4], rva, return42, []));
            Check.Equal(42, ((delegate* unmanaged<int>)(anchor + rva))());
            byte[] return43 = [0xb8, 43, 0, 0, 0, 0xc3];
            MemoryPatchTransaction.Apply(memory, [new("Retarget own fixture", rva, return42, return43)]);
            Check.Equal(43, ((delegate* unmanaged<int>)(anchor + rva))());
        }
        finally { runtime::Dollhouse.Runtime.WindowsMemory.VirtualFree(allocation, 0, 0x8000); }
    }

    private static void VerifyTransaction()
    {
        var edits = new[] { new MemoryEdit("first", 10, [1, 2], [3, 4]), new MemoryEdit("second", 4095, [5, 6], [7, 8]) };
        foreach (string failure in new[] { "none", "validation", "protection", "write", "flush" })
        {
            var memory = new FakeMemory(failure);
            if (failure == "none") MemoryPatchTransaction.Apply(memory, edits);
            else
            {
                bool failed = false;
                try { MemoryPatchTransaction.Apply(memory, edits); } catch (Exception error) when (error is IOException or InvalidDataException) { failed = true; }
                Check.True(failed, "Injected memory failure must stop the transaction.");
            }
            Check.True(memory.Protections.All(value => value == 0x20), "Page protections must be restored.");
            if (failure != "none" && failure != "validation")
            {
                Check.True(memory.Bytes.AsSpan(10, 2).SequenceEqual(new byte[] { 1, 2 }), "First edit must roll back.");
                Check.True(memory.Bytes.AsSpan(4095, 2).SequenceEqual(new byte[] { 5, 6 }), "Cross-page edit must roll back.");
            }
        }
    }

    private sealed class FakeMemory(string failure) : IPatchMemory
    {
        internal readonly byte[] Bytes = Initialize(failure);
        internal readonly uint[] Protections = [0x20, 0x20];
        private bool injected;
        private static byte[] Initialize(string failure)
        {
            var bytes = new byte[8192]; bytes[10] = 1; bytes[11] = 2; bytes[4095] = 5; bytes[4096] = 6;
            if (failure == "validation") bytes[10] = 9;
            return bytes;
        }
        public byte[] Read(int rva, int length) => Bytes.AsSpan(rva, length).ToArray();
        public void Write(int rva, byte[] bytes)
        {
            if (failure == "write" && rva == 4095 && !injected)
            { injected = true; Bytes[rva] = bytes[0]; throw new IOException("Injected partial write."); }
            bytes.CopyTo(Bytes, rva);
        }
        public uint Protect(int rva, int length, uint protection)
        {
            if (failure == "protection" && rva == 4096 && protection == 0x40 && !injected)
            { injected = true; throw new IOException("Injected protection failure."); }
            uint previous = Protections[rva / 4096]; Protections[rva / 4096] = protection; return previous;
        }
        public void Flush()
        {
            if (failure == "flush" && !injected) { injected = true; throw new IOException("Injected flush failure."); }
        }
    }

    private static void VerifyCurrentPlan()
    {
        if (GameBinaryFixture.Original is not { } fixture) return;
        var original = File.ReadAllBytes(fixture);
        using var image = new PeImage(original);
        using var pe = new PEReader(new MemoryStream(original, false));
        int config = image.FileOffset(pe.PEHeaders.PEHeader!.LoadConfigTableDirectory.RelativeVirtualAddress);
        Check.Equal((ushort)0, BinaryPrimitives.ReadUInt16LittleEndian(original.AsSpan(config + 78)));
        var connection = RuntimePatchPlan.ConnectionEdits(original,
            new(new string('a', 64), GameBuild.OldLicenseRoot), _ => 0x1000, _ => 0x2000);
        Check.Equal(11, connection.Count);
        Check.Equal(8, connection.Count(edit => edit.Name.StartsWith("Device crypto: ")));
        var layout = GameplayRuntimePatch.Plan.BuildMemory(original, 0x60000000, _ => unchecked((nint)0x7ff000001000));
        RuntimeRecipeTests.Verify(original, layout, 0x60000000);
        Check.True(layout.Edits.Count > connection.Count, "Runtime must include all gameplay repairs.");
        Check.True(layout.CodeRva - 0x60000000 + layout.Code.Length <= 65536, "Runtime arena exceeds its allocation.");
        foreach (var edit in layout.Edits)
            Check.True(original.AsSpan(image.FileOffset(edit.Rva), edit.Expected.Length).SequenceEqual(edit.Expected), "Full original preimages are retained.");
        foreach (var name in new[] { "DollhouseUseItem", "DollhouseExecFemaleThreshold", "DollhousePersistCumflationWithContext", "DollhouseBuildSettingsMenu", "DollhouseSemenCapacity" })
            Check.True(GameplayRuntimePatch.Plan.ExportNames.Contains(name), "Runtime omits feature export: " + name);
        int damaged = image.FileOffset(layout.Edits[0].Rva);
        original[damaged] ^= 1;
        Check.Throws<InvalidDataException>(() => GameplayRuntimePatch.Plan.BuildMemory(original, 0x60000000, _ => 0x2000));
    }
    private static void VerifyExports()
    {
        if (!OperatingSystem.IsWindows() || !RuntimeCompatibility.IsBundled) return;
        // Inspect native exports without running DLL initialization here.
        var path = Path.Combine(Dollhouse.Core.WorkspacePaths.Discover().Root, "artifacts", "runtime", RuntimeCompatibility.LibraryName);
        if (!File.Exists(path)) return;
        var bytes = File.ReadAllBytes(path);
        using var pe = new PEReader(new MemoryStream(bytes, false));
        using var image = new PeImage(bytes);
        int directory = image.FileOffset(pe.PEHeaders.PEHeader!.ExportTableDirectory.RelativeVirtualAddress);
        int count = BitConverter.ToInt32(bytes, directory + 24);
        int names = image.FileOffset(BitConverter.ToInt32(bytes, directory + 32));
        var exports = new HashSet<string>();
        for (int index = 0; index < count; index++)
        {
            int start = image.FileOffset(BitConverter.ToInt32(bytes, names + index * 4));
            int end = Array.IndexOf(bytes, (byte)0, start);
            exports.Add(Encoding.ASCII.GetString(bytes, start, end - start));
        }
        foreach (var name in GameplayRuntimePatch.Plan.ExportNames.Concat(new[] {
            "DollhouseMain", "NCryptOpenStorageProvider", "NCryptOpenKey", "NCryptCreatePersistedKey", "NCryptSetProperty",
            "NCryptFinalizeKey", "NCryptExportKey", "NCryptSignHash", "NCryptFreeObject" }))
            Check.True(exports.Contains(name), "Missing unified export: " + name);
    }
}
