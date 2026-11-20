using System.Runtime.InteropServices;
using Dollhouse.Gameplay;
using SupportedBuild = Dollhouse.Gameplay.SupportedBuild;

namespace Dollhouse.Tests;

internal static unsafe class CumflationNotificationTests
{
    private const nint Context = 0x1000, Roles = 0x2000, LocalRoles = 0x4000;
    private const nint FirstWorld = 0x7000, SecondWorld = 0x8000;
    private static nint worldAddress;
    private static int worldReads;
    private static int womb, gi, allocations, frees;
    private static bool missingProxy, failBuilder;
    private static readonly List<string> Events = [];
    private static readonly List<double> Fullness = [];
    private static readonly List<string> Logs = [];
    private static readonly List<nint> ResolutionContexts = [];

    public static void Verify()
    {
        using var script = new ScriptCallFixture();
        script.OnExecute = () => Events.Add("experiment");
        Check.Equal(0x60, sizeof(LocalRoleAttributesChanged));
        Check.Equal(0x68, sizeof(RoomRoleAttributesChanged));
        Check.Equal(8, Marshal.OffsetOf<RoomRoleAttributesChanged>(nameof(RoomRoleAttributesChanged.Role)).ToInt32());
        Check.Equal(0x18, Marshal.OffsetOf<RoomRoleAttributesChanged>(nameof(RoomRoleAttributesChanged.Attributes)).ToInt32());
        nint activeWorld = FirstWorld;
        worldAddress = (nint)(&activeWorld); worldReads = 0;
        womb = gi = allocations = frees = 0; missingProxy = failBuilder = false;
        Events.Clear(); Fullness.Clear(); Logs.Clear(); ResolutionContexts.Clear();
        char* name = stackalloc char[2] { 'A', '\0' };
        byte* entry = stackalloc byte[16]; new Span<byte>(entry, 16).Clear();
        *entry = 74; *(int*)(entry + 4) = 1500;
        RolePropertyChange change = new()
        {
            Role = new() { Data = name, Length = 2, Capacity = 2 },
            Attributes = new() { Elements = entry, SlotCount = 1, SlotCapacity = 1, AllocationBitCount = 1 }
        };
        change.Attributes.InlineAllocationFlags[0] = 1;
        RolePropertyChanges changes = new() { Data = &change, Length = 1, Capacity = 1 };
        byte[] borrowed = new ReadOnlySpan<byte>(&change, sizeof(RolePropertyChange)).ToArray();
        var game = new NativeGame(0, rva => script.Resolve(rva) is var binding && binding != 0 ? binding : Resolve(rva));
        game.HandleRolePropertyChanges(7, &changes, Context);
        Check.Equal(1000, womb); Check.Equal(1.0, Fullness.Single());
        // The actual rehost action singleton has a transient-package owner and no world.
        // Neither an owner nor a manager API is bound here; only the engine's live world works.
        Check.True(ResolutionContexts.SequenceEqual(new[] { Context, FirstWorld }), "Active world lookup was skipped.");
        Check.Equal(2, worldReads); // Role lookup and targeted script refresh each read the current world.
        Check.Equal("write:74,room,experiment", string.Join(',', Events));
        Check.Equal(7, script.Argument);
        Check.Equal(1, script.Executions);
        Check.Equal(allocations, frees);
        Check.True(new ReadOnlySpan<byte>(&change, sizeof(RolePropertyChange)).SequenceEqual(borrowed), "Notification borrowed request ownership.");
        game.HandleRolePropertyChanges(7, &changes, Context);
        Check.Equal(1, Fullness.Count); // A full body must not produce repeated change notifications.
        game.HandleRolePropertyChanges(99, &changes, Context);
        Check.Equal(1, Fullness.Count);
        Check.Equal(1, script.Executions);

        // Native clear already wrote zeros. Its local listeners run first; then the room sees
        // the decrease which drives the existing chair's positive-to-zero visual transition.
        womb = gi = 0; Events.Clear(); ResolutionContexts.Clear(); worldReads = 0;
        LocalRoleAttributesChanged local = new() { Role = change.Role };
        byte[] localBefore = new ReadOnlySpan<byte>(&local, sizeof(LocalRoleAttributesChanged)).ToArray();
        game.NotifyVolumesCleared(LocalRoles + SupportedBuild.LocalRoleAttributesDelegate, &local);
        Check.Equal("local,room", string.Join(',', Events));
        Check.Equal(1, script.Executions); // Preserve native gradual draining, without an immediate refresh.
        Check.True(ResolutionContexts.SequenceEqual(new[] { LocalRoles }), "Working clear context should resolve directly.");
        Check.Equal(0, worldReads);
        Check.Equal(0.0, Fullness.Last()); Check.Equal(allocations, frees);
        Check.True(new ReadOnlySpan<byte>(&local, sizeof(LocalRoleAttributesChanged)).SequenceEqual(localBefore), "Native clear parameters mutated.");

        // A map transition changes the engine's world pointer. The next fill must use the new
        // world, while missing/tearing-down worlds must preserve storage without publishing.
        activeWorld = SecondWorld; Events.Clear(); ResolutionContexts.Clear();
        game.HandleRolePropertyChanges(0, &changes, Context);
        Check.True(ResolutionContexts.SequenceEqual(new[] { Context, SecondWorld }), "A stale world was cached across map changes.");
        Check.Equal(1.0, Fullness.Last());
        womb = gi = 0; activeWorld = 0; Events.Clear(); ResolutionContexts.Clear();
        game.HandleRolePropertyChanges(0, &changes, Context);
        Check.Equal("write:74", string.Join(',', Events));
        Check.True(ResolutionContexts.SequenceEqual(new[] { Context }), "Null world was passed to native resolver.");
        Check.True(Logs.Any(line => line.Contains("world is unavailable", StringComparison.Ordinal)), "Null-world status was silent.");
        activeWorld = FirstWorld; womb = gi = 0;
        Events.Clear(); missingProxy = true;
        game.HandleRolePropertyChanges(0, &changes, Context);
        Check.Equal("write:74", string.Join(',', Events)); // Persistence survives absent world/proxy.
        Check.True(Logs.Last().Contains("proxy was unavailable", StringComparison.Ordinal), "Missing-proxy status was silent.");
        womb = gi = 0; Events.Clear();
        game.NotifyVolumesCleared(LocalRoles + SupportedBuild.LocalRoleAttributesDelegate, &local);
        Check.Equal("local", string.Join(',', Events)); // Existing clear listeners still run without a room.
        Check.Equal(allocations, frees);
        missingProxy = false;

        // A managed binding failure still releases the owned name and preserves local clear listeners.
        failBuilder = true; Events.Clear();
        nint parameters = (nint)(&local);
        Check.Throws<InvalidOperationException>(() => game.NotifyVolumesCleared(LocalRoles + SupportedBuild.LocalRoleAttributesDelegate,
            (LocalRoleAttributesChanged*)parameters));
        Check.Equal("local", string.Join(',', Events)); Check.Equal(allocations, frees);
        failBuilder = false; worldAddress = 0;
    }

    private static nint Resolve(int rva) => rva switch
    {
        SupportedBuild.LocalPlayerId => (nint)(delegate* unmanaged<int>)&Player,
        SupportedBuild.LocalProgressionGate => (nint)(delegate* unmanaged<byte>)&Mode,
        SupportedBuild.ReadRoleAttribute => (nint)(delegate* unmanaged<GameString*, int, int>)&Read,
        SupportedBuild.WriteRoleAttribute => (nint)(delegate* unmanaged<GameString*, int, int, void>)&Write,
        SupportedBuild.RolesClass => (nint)(delegate* unmanaged<nint>)&Class,
        SupportedBuild.FindPlayerProxy => (nint)(delegate* unmanaged<nint, int, nint, nint>)&Find,
        SupportedBuild.CurrentWorld => WorldStorage(),
        SupportedBuild.CopyString => (nint)(delegate* unmanaged<GameString*, GameString*, nint>)&Copy,
        SupportedBuild.Free => (nint)(delegate* unmanaged<void*, void>)&Free,
        SupportedBuild.AddRoleAttribute when !failBuilder =>
            (nint)(delegate* unmanaged<NativeAttributeMap*, int*, AttributePairReferences*, byte*, void>)&Add,
        SupportedBuild.Broadcast => (nint)(delegate* unmanaged<nint, void*, void>)&Broadcast,
        SupportedBuild.GameplayLog => (nint)(delegate* unmanaged<char*, void>)&Log,
        _ => throw new InvalidOperationException("Synthetic native binding failure.")
    };

    private static nint WorldStorage() { worldReads++; return worldAddress; }

    [UnmanagedCallersOnly] private static int Player() => 7;
    [UnmanagedCallersOnly] private static byte Mode() => 0;
    [UnmanagedCallersOnly] private static nint Class() => 0x3000;
    [UnmanagedCallersOnly] private static nint Find(nint context, int playerId, nint type)
    {
        Check.True(context == Context || context == LocalRoles || context == FirstWorld || context == SecondWorld, "Unrelated world context inspected.");
        Check.Equal(7, playerId); Check.Equal((nint)0x3000, type);
        ResolutionContexts.Add(context);
        return !missingProxy && context != Context ? Roles : 0;
    }
    [UnmanagedCallersOnly] private static int Read(GameString* role, int key) => key switch { 72 => 1000, 73 => gi, 74 => womb, _ => -1 };
    [UnmanagedCallersOnly] private static void Write(GameString* role, int key, int value)
    {
        if (key == 74) womb = value; else if (key == 73) gi = value; else Environment.FailFast("Unexpected attribute.");
        Events.Add($"write:{key}");
    }
    [UnmanagedCallersOnly] private static nint Copy(GameString* destination, GameString* source)
    {
        *destination = *source;
        destination->Data = (char*)NativeMemory.Alloc((nuint)source->Length, 2); allocations++;
        new ReadOnlySpan<char>(source->Data, source->Length).CopyTo(new Span<char>(destination->Data, source->Length));
        return (nint)destination;
    }
    [UnmanagedCallersOnly] private static void Free(void* memory) { NativeMemory.Free(memory); frees++; }
    [UnmanagedCallersOnly] private static void Log(char* text) => Logs.Add(new string(text));
    [UnmanagedCallersOnly] private static void Add(NativeAttributeMap* map, int* element, AttributePairReferences* pair, byte* existing)
    {
        Check.True(existing == null, "Unexpected replace flag.");
        if (map->Elements == null)
        {
            Check.Equal(-1, map->FirstFreeIndex); Check.Equal(128, map->AllocationBitCapacity);
            map->Elements = (byte*)NativeMemory.AllocZeroed(32); allocations++; map->SlotCapacity = 2;
        }
        int index = map->SlotCount++;
        Check.True(index < 2, "Unexpected notification attributes.");
        byte* slot = map->Elements + 16 * index;
        *slot = *pair->Key; *(int*)(slot + 4) = *pair->Value;
        map->AllocationBitCount = map->SlotCount; map->InlineAllocationFlags[0] |= 1u << index;
        *element = index;
    }
    [UnmanagedCallersOnly] private static void Broadcast(nint storage, void* parameters)
    {
        if (storage == LocalRoles + SupportedBuild.LocalRoleAttributesDelegate) { Events.Add("local"); return; }
        Check.Equal(Roles + SupportedBuild.RoomRoleAttributesDelegate, storage);
        var notification = (RoomRoleAttributesChanged*)parameters;
        Check.Equal(7, notification->PlayerId);
        Check.Equal("A", new string(notification->Role.Data, 0, notification->Role.Length - 1));
        Check.True(notification->Attributes.TryGet(74, out int actualWomb), "Missing womb value.");
        Check.True(notification->Attributes.TryGet(73, out int actualGi), "Missing GI value.");
        Check.Equal(womb, actualWomb); Check.Equal(gi, actualGi);
        Check.True(!notification->Attributes.TryGet(72, out _), "Capacity must remain a read policy.");
        Fullness.Add((actualWomb + actualGi) / 1000.0); Events.Add("room");
    }
}
