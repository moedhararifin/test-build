using System.Globalization;
using System.Runtime.InteropServices;
using Dollhouse.Gameplay;

namespace Dollhouse.Tests;

internal sealed unsafe class SexStatsTests : IDisposable
{
    private static SexStatsTests current = null!;
    private readonly TemporaryWorkspace workspace = new();
    private readonly Dictionary<(string Role, int Key), int> values = [];
    private readonly List<nint> allocations = [];
    private readonly List<int> reads = [], writes = [];
    private readonly byte progression;
    private readonly string settingsPath, attributesPath;
    private int originalCalls;
    private bool failRead;

    private SexStatsTests(byte progression)
    {
        current = this; this.progression = progression;
        settingsPath = Path.Combine(workspace.Paths.Root, "settings.json");
        attributesPath = Path.Combine(workspace.Paths.Root, "synthetic-stats.tsv");
        Check.True(new GameplaySettingsStore(settingsPath, _ => { }).Commit(new(false)), "Could not arrange OFF.");
        Environment.SetEnvironmentVariable(SettingsBindings.PathVariable, settingsPath);
    }

    public void Dispose()
    {
        foreach (nint allocation in allocations) NativeMemory.Free((void*)allocation);
        workspace.Dispose();
    }

    private nint Allocate(int bytes)
    {
        nint memory = (nint)NativeMemory.AllocZeroed((nuint)bytes);
        allocations.Add(memory); return memory;
    }

    private RolePropertyChange Change(string role, params (int Key, int Delta)[] attributes)
    {
        char* name = (char*)Allocate((role.Length + 1) * 2);
        role.AsSpan().CopyTo(new Span<char>(name, role.Length));
        int slots = attributes.Length + 1;
        byte* entries = (byte*)Allocate(slots * 16);
        entries[0] = 63; *(int*)(entries + 4) = 99999; // Freed slot must not contribute.
        NativeAttributeMap map = new() { Elements = entries, SlotCount = slots, SlotCapacity = slots, AllocationBitCount = slots };
        for (int i = 0; i < attributes.Length; i++)
        {
            int slot = i + 1;
            entries[slot * 16] = checked((byte)attributes[i].Key);
            *(int*)(entries + slot * 16 + 4) = attributes[i].Delta;
            map.InlineAllocationFlags[slot / 32] |= 1U << (slot % 32);
        }
        return new() { Role = new() { Data = name, Length = role.Length + 1, Capacity = role.Length + 1 }, Attributes = map };
    }

    private RolePropertyChanges* Request(params RolePropertyChange[] changes)
    {
        var data = (RolePropertyChange*)Allocate(changes.Length * sizeof(RolePropertyChange));
        changes.AsSpan().CopyTo(new Span<RolePropertyChange>(data, changes.Length));
        var request = (RolePropertyChanges*)Allocate(sizeof(RolePropertyChanges));
        *request = new() { Data = data, Length = changes.Length, Capacity = changes.Length };
        return request;
    }

    private NativeGame Game() => new(0, rva => rva switch
    {
        SupportedBuild.LocalPlayerId => (nint)(delegate* unmanaged<int>)&Player,
        SupportedBuild.LocalProgressionGate => (nint)(delegate* unmanaged<byte>)&Mode,
        SupportedBuild.LocalRolePropertyChange => (nint)(delegate* unmanaged<int, RolePropertyChanges*, void>)&Original,
        SupportedBuild.ReadRoleAttribute when !failRead => (nint)(delegate* unmanaged<GameString*, int, int>)&Read,
        SupportedBuild.WriteRoleAttribute => (nint)(delegate* unmanaged<GameString*, int, int, void>)&Write,
        SupportedBuild.GameplayLog => (nint)(delegate* unmanaged<char*, void>)&Log,
        _ => throw new InvalidOperationException("Synthetic stats binding failure.")
    });

    internal static void Verify()
    {
        string? previous = Environment.GetEnvironmentVariable(SettingsBindings.PathVariable);
        try
        {
            foreach (byte mode in new byte[] { 0, 1 })
            {
                using var f = new SexStatsTests(mode);
                f.VerifyToggleAndPersistence();
            }
            using var errors = new SexStatsTests(1);
            errors.VerifyBoundsAndFailures();
        }
        finally { Environment.SetEnvironmentVariable(SettingsBindings.PathVariable, previous); }
    }

    private void VerifyToggleAndPersistence()
    {
        values[("A", 65)] = 50000; values[("A", 73)] = 100; values[("A", 74)] = 50;
        values[("A", 57)] = 3; values[("A", 72)] = 9999; values[("B", 63)] = 10;
        var attributes = Enumerable.Range(58, 12).Select((key, i) => (key, i + 1))
            .Concat(new[] { (57, 999), (72, 888), (73, 600), (74, 1000) }).ToArray();
        RolePropertyChange first = Change("A", attributes), second = Change("B", (63, 2));
        var request = Request(first, second);
        byte[] before = new ReadOnlySpan<byte>(request->Data, request->Length * sizeof(RolePropertyChange)).ToArray();
        byte[] mapBefore = new ReadOnlySpan<byte>(first.Attributes.Elements, first.Attributes.SlotCount * 16).ToArray();
        var game = Game();
        game.HandleRolePropertyChanges(7, request);
        Check.Equal(12, values[("B", 63)]); // OFF on the first record must not skip later statistics.
        Check.True(reads.All(key => key is >= 58 and <= 69), "OFF read current contents or capacity.");
        Check.Equal(100, values[("A", 73)]); Check.Equal(50, values[("A", 74)]);
        game.HandleRolePropertyChanges(7, request); // Identical legitimate events must both count.
        Check.Equal(14, values[("B", 63)]);
        SetEnabled(game, true);
        game.HandleRolePropertyChanges(7, Request(first));
        Check.Equal(900, values[("A", 74)]); Check.Equal(100, values[("A", 73)]);
        SetEnabled(game, false);
        game.HandleRolePropertyChanges(7, request);
        Reload();
        game = Game(); // Reopen settings and reconstruct the native attribute cache.
        game.HandleRolePropertyChanges(0, request);
        for (int key = 58; key <= 69; key++)
            Check.Equal((key == 65 ? 50000 : 0) + 5 * (key - 57), values[("A", key)]);
        Check.Equal(18, values[("B", 63)]);
        Check.Equal(900, values[("A", 74)]); Check.Equal(100, values[("A", 73)]);
        Check.Equal(3, values[("A", 57)]); Check.Equal(9999, values[("A", 72)]);
        Check.Equal(progression == 0 ? 0 : 5, originalCalls);
        int writeCount = writes.Count;
        game.HandleRolePropertyChanges(99, request);
        Check.Equal(writeCount, writes.Count); // Foreign players cannot mutate local statistics.
        Check.True(new ReadOnlySpan<byte>(request->Data, before.Length).SequenceEqual(before), "Statistics mutated the borrowed request.");
        Check.True(new ReadOnlySpan<byte>(first.Attributes.Elements, mapBefore.Length).SequenceEqual(mapBefore), "Statistics mutated its sparse input map.");
        Check.True(writes.All(key => key is >= 58 and <= 69 or 73 or 74), "An unrelated attribute was written.");
    }

    private void VerifyBoundsAndFailures()
    {
        var game = Game();
        values[("A", 63)] = int.MaxValue - 1; values[("A", 58)] = 3;
        game.HandleRolePropertyChanges(7, Request(Change("A", (63, 10), (58, -10), (68, -1), (69, 0), (70, 999))));
        Check.Equal(int.MaxValue, values[("A", 63)]); Check.Equal(0, values[("A", 58)]);
        Check.Equal(2, writes.Count); Check.True(!values.ContainsKey(("A", 70)), "Reserved stat key accepted.");
        RolePropertyChange badRole = Change("A", (60, 1)); badRole.Role.Capacity = 1;
        game.HandleRolePropertyChanges(7, Request(badRole)); Check.Equal(2, writes.Count);
        var badMap = Change("A", (60, 1)); badMap.Attributes.AllocationBitCount = 0;
        game.HandleRolePropertyChanges(7, Request(badMap)); Check.Equal(2, writes.Count);
        int originals = originalCalls;
        var request = Request(Change("A", (60, 1)));
        failRead = true;
        try { game.HandleRolePropertyChanges(7, request); throw new Exception("Expected native reader failure."); }
        catch (InvalidOperationException) { Check.Equal(originals + 1, originalCalls); }
        finally { failRead = false; }
    }

    private static void SetEnabled(NativeGame game, bool enabled)
    {
        fixed (char* key = SettingsBindings.CumflationKey, text = enabled ? "1" : "0")
        {
            GameString value = new() { Data = text, Length = 2, Capacity = 2 };
            game.SetSetting(key, &value);
        }
    }

    private void Reload()
    {
        values.Clear();
        foreach (string line in File.ReadAllLines(attributesPath))
        {
            string[] fields = line.Split('\t');
            values[(fields[0], int.Parse(fields[1], CultureInfo.InvariantCulture))] = int.Parse(fields[2], CultureInfo.InvariantCulture);
        }
    }

    [UnmanagedCallersOnly] private static int Player() => 7;
    [UnmanagedCallersOnly] private static byte Mode() => current.progression;
    [UnmanagedCallersOnly] private static void Original(int player, RolePropertyChanges* request) => current.originalCalls++;
    [UnmanagedCallersOnly] private static void Log(char* text) { }
    [UnmanagedCallersOnly] private static int Read(GameString* role, int key)
    {
        current.reads.Add(key);
        return key == 72 ? 1000 : current.values.GetValueOrDefault((new string(role->Data, 0, role->Length - 1), key));
    }
    [UnmanagedCallersOnly] private static void Write(GameString* role, int key, int value)
    {
        current.writes.Add(key);
        current.values[(new string(role->Data, 0, role->Length - 1), key)] = value;
        File.WriteAllLines(current.attributesPath, current.values.Select(pair =>
            FormattableString.Invariant($"{pair.Key.Role}\t{pair.Key.Key}\t{pair.Value}")));
    }
}
