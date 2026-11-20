using System.Runtime.InteropServices;
using Dollhouse.Gameplay;

namespace Dollhouse.Tests;

internal static unsafe class CumflationSettingTests
{
    private static int womb, gi, reads, writes, originalCalls;
    private static readonly List<string> logs = [];
    internal static void Verify()
    {
        SexStatsTests.Verify();
        using var workspace = new TemporaryWorkspace();
        string path = Path.Combine(workspace.Paths.Root, "settings.json");
        var store = new GameplaySettingsStore(path, logs.Add);
        Check.True(store.Commit(new(false)), "Could not arrange persisted OFF setting.");
        string? previous = Environment.GetEnvironmentVariable(SettingsBindings.PathVariable);
        Environment.SetEnvironmentVariable(SettingsBindings.PathVariable, path);
        try
        {
            logs.Clear(); womb = 200; gi = 100; reads = writes = originalCalls = 0;
            var game = Game();
            char* name = stackalloc char[] { 'A', '\0' };
            byte* entry = stackalloc byte[16]; new Span<byte>(entry, 16).Clear();
            entry[0] = 74; *(int*)(entry + 4) = 700;
            RolePropertyChange change = new()
            {
                Role = new() { Data = name, Length = 2, Capacity = 2 },
                Attributes = new() { Elements = entry, SlotCount = 1, SlotCapacity = 1, AllocationBitCount = 1 }
            };
            change.Attributes.InlineAllocationFlags[0] = 1;
            RolePropertyChanges request = new() { Data = &change, Length = 1, Capacity = 1 };
            byte[] before = new ReadOnlySpan<byte>(&change, sizeof(RolePropertyChange)).ToArray();
            game.HandleRolePropertyChanges(7, &request);
            Check.Equal(0, reads); Check.Equal(0, writes); Check.Equal(1, originalCalls);
            Check.Equal(200, womb); Check.Equal(100, gi);
            Check.True(new ReadOnlySpan<byte>(&change, sizeof(RolePropertyChange)).SequenceEqual(before), "OFF mutated the borrowed request.");
            Check.True(!logs.Any(x => x.Contains("incoming fluid volume ignored")), "OFF must not log every skipped fill.");

            // Applying in the same running instance takes effect on the next callback.
            Select(game, true);
            game.HandleRolePropertyChanges(7, &request);
            Check.Equal(900, womb); Check.Equal(100, gi); Check.Equal(1, writes); Check.Equal(2, originalCalls);
            Select(game, false);
            game.HandleRolePropertyChanges(7, &request);
            Check.Equal(900, womb); Check.Equal(1, writes); Check.Equal(3, originalCalls);
            Check.True(!new GameplaySettingsStore(path, logs.Add).Current.CumflationEnabled, "OFF was not saved for the next launch.");
            var reopened = Game();
            reopened.HandleRolePropertyChanges(7, &request);
            Check.Equal(900, womb); Check.Equal(1, writes); Check.Equal(4, originalCalls);

            // Native chair clear remains a separate path: its existing zero writes still run.
            game.CaptureDrainAndWrite(&change.Role, 73, 0);
            ((delegate* unmanaged<GameString*, int, int, void>)&Write)(&change.Role, 74, 0);
            game.NotifyVolumesCleared(0, null);
            Check.Equal(0, womb); Check.Equal(0, gi); Check.Equal(3, writes);
        }
        finally { Environment.SetEnvironmentVariable(SettingsBindings.PathVariable, previous); }
    }
    private static NativeGame Game() => new(0, rva => rva switch
    {
        SupportedBuild.LocalPlayerId => (nint)(delegate* unmanaged<int>)&Player,
        SupportedBuild.ReadRoleAttribute => (nint)(delegate* unmanaged<GameString*, int, int>)&Read,
        SupportedBuild.WriteRoleAttribute => (nint)(delegate* unmanaged<GameString*, int, int, void>)&Write,
        SupportedBuild.LocalProgressionGate => (nint)(delegate* unmanaged<byte>)&Progression,
        SupportedBuild.LocalRolePropertyChange => (nint)(delegate* unmanaged<int, RolePropertyChanges*, void>)&Original,
        SupportedBuild.GameplayLog => (nint)(delegate* unmanaged<char*, void>)&Log,
        _ => throw new InvalidOperationException("Unexpected settings/native API.")
    });
    private static void Select(NativeGame game, bool enabled)
    {
        fixed (char* key = SettingsBindings.CumflationKey)
        fixed (char* text = enabled ? "1" : "0")
        {
            GameString value = new() { Data = text, Length = 2, Capacity = 2 };
            game.SetSetting(key, &value);
        }
    }
    [UnmanagedCallersOnly] private static int Player() => 7;
    [UnmanagedCallersOnly] private static byte Progression() => 1;
    [UnmanagedCallersOnly] private static void Original(int player, RolePropertyChanges* request) => originalCalls++;
    [UnmanagedCallersOnly] private static int Read(GameString* role, int key) { reads++; return key == 72 ? 1000 : key == 74 ? womb : gi; }
    [UnmanagedCallersOnly] private static void Write(GameString* role, int key, int value) { writes++; if (key == 74) womb = value; else gi = value; }
    [UnmanagedCallersOnly] private static void Log(char* text) => logs.Add(new string(text));
}
