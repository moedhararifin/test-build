using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using Dollhouse.App;
using Dollhouse.Core.Patching;
using Dollhouse.Core.Patching.Native;
using Dollhouse.Gameplay;
using Iced.Intel;
using SupportedBuild = Dollhouse.Gameplay.SupportedBuild;

namespace Dollhouse.Tests;

internal static unsafe class CumflationPatchTests
{
    private static readonly Dictionary<string, int[]> Attributes = [];
    private static readonly Dictionary<string, int> Stamina = [];
    private static string storeFile = "";
    private static byte progression;
    private static int writes, originalCalls;
    private static bool failRead, fixedCapacity;

    public static void Verify()
    {
        Check.Equal((1100, 200), CumflationPatch.Accumulate(2000, 100, 200, 1000, 0));
        Check.Equal((1100, 900), CumflationPatch.Accumulate(2000, 1100, 200, 0, 1000));
        Check.Equal((2000, 0), CumflationPatch.Accumulate(2000, 0, 0, int.MaxValue, int.MaxValue));
        Check.Equal((2000, 2000), CumflationPatch.Accumulate(2000, 2000, 2000, 100, 100));
        Check.Equal((100, 200), CumflationPatch.Accumulate(0, 100, 200, 100, 100));
        Check.Equal((100, 200), CumflationPatch.Accumulate(2000, 100, 200, -500, 0));
        Check.Equal((int.MaxValue, 0), CumflationPatch.Accumulate(int.MaxValue, 0, 0, int.MaxValue, int.MaxValue));
        Check.Throws<ArgumentException>(() => AppOptions.Parse(["--game-patch", "cumflation"]));
        Check.Throws<ArgumentException>(() => AppOptions.Parse(["--game-patch", "cumflation", "--game-patch", "cumflation"]));
        Check.Throws<ArgumentException>(() => AppOptions.Parse(["--clean", "--game-patch", "cumflation"]));
        VerifySparseMap();
        VerifyNativeBindings();
        VerifyMissingCapacity();
        VerifyCharacterCapacities();
        CumflationNotificationTests.Verify();
        ScriptCallFixture.Verify();
        DrainAlertFixture.Verify();
        VerifyNativeEvidence();
        if (Environment.GetEnvironmentVariable("DOLLHOUSE_TEST_GAMEPLAY") is { } path)
        {
            nint library = NativeLibrary.Load(Path.GetFullPath(path)); // Our DLL only, never load game code.
            VerifyNativeCapacity(library);
            nint contextExport = NativeLibrary.GetExport(library, "DollhousePersistCumflationWithContext");
            ((delegate* unmanaged<int, RolePropertyChanges*, nint, void>)contextExport)(0, null, 0);
            nint cleared = NativeLibrary.GetExport(library, "DollhouseNotifyCumflationCleared");
            ((delegate* unmanaged<nint, LocalRoleAttributesChanged*, void>)cleared)(0, null);
            Check.True(NativeLibrary.GetExport(library, "DollhouseTickCumflation") != 0, "Missing frame wrapper export.");
            foreach (string name in new[] { "DollhouseCaptureDrain", "DollhouseDispatchDrainCleared", "DollhouseShowDrainRightMsg" })
                Check.True(NativeLibrary.GetExport(library, name) != 0, "Missing drain alert export: " + name);
        }
    }

    private static void SetEntry(byte* entries, int slot, byte key, int value)
    {
        entries[slot * 16] = key;
        *(int*)(entries + slot * 16 + 4) = value;
    }

    private static void VerifySparseMap()
    {
        Check.Equal(0x50, sizeof(NativeAttributeMap));
        Check.Equal(0x68, sizeof(RolePropertyChange));
        Check.Equal(16, sizeof(RolePropertyChanges));
        Check.Equal(0x10, Marshal.OffsetOf<RolePropertyChange>(nameof(RolePropertyChange.Attributes)).ToInt32());
        Check.Equal(0x60, Marshal.OffsetOf<RolePropertyChange>(nameof(RolePropertyChange.AddedFavor)).ToInt32());
        byte* slots = stackalloc byte[130 * 16];
        SetEntry(slots, 0, 74, 99999); // Freed slot: must never be read as a live value.
        SetEntry(slots, 65, 74, 400);
        NativeAttributeMap map = new() { Elements = slots, SlotCount = 66, SlotCapacity = 130, AllocationBitCount = 66 };
        map.InlineAllocationFlags[2] = 2;
        Check.True(map.TryGet(74, out int value) && value == 400, "Inline sparse-map membership ignored.");
        Check.True(!map.TryGet(73, out _), "Absent attribute appeared present.");
        uint* flags = stackalloc uint[5];
        new Span<uint>(flags, 5).Clear(); flags[4] = 2;
        SetEntry(slots, 129, 73, 600);
        map.HeapAllocationFlags = flags; map.SlotCount = map.AllocationBitCount = 130;
        Check.True(map.TryGet(73, out value) && value == 600, "Heap allocation flags ignored.");
        map.HeapAllocationFlags = null;
        Check.True(!map.TryGet(73, out _), "Invalid inline flag extent accepted.");
        map.AllocationBitCount = 0;
        Check.True(!map.TryGet(74, out _), "Inconsistent native map accepted.");
    }

    private static NativeGame Game() => new(0, rva => rva switch
    {
        SupportedBuild.LocalPlayerId => (nint)(delegate* unmanaged<int>)&Player,
        SupportedBuild.LocalProgressionGate => (nint)(delegate* unmanaged<byte>)&Mode,
        SupportedBuild.LocalRolePropertyChange => (nint)(delegate* unmanaged<int, RolePropertyChanges*, void>)&Original,
        SupportedBuild.ReadRoleAttribute when !failRead => (nint)(delegate* unmanaged<GameString*, int, int>)&Read,
        SupportedBuild.WriteRoleAttribute => (nint)(delegate* unmanaged<GameString*, int, int, void>)&Write,
        SupportedBuild.ReadStoredStamina => (nint)(delegate* unmanaged<GameString*, int*, int*, void>)&ReadStamina,
        SupportedBuild.DeductStamina => (nint)(delegate* unmanaged<GameString*, int, char*, void>)&SpendStamina,
        SupportedBuild.GameplayLog => (nint)(delegate* unmanaged<char*, void>)&FatigueLog,
        _ => throw new InvalidOperationException("Synthetic native reader failure.")
    });

    private static void VerifyNativeBindings()
    {
        using var temporary = new TemporaryWorkspace();
        storeFile = Path.Combine(temporary.Paths.Root, "synthetic-attributes.txt");
        Attributes.Clear(); Stamina.Clear(); Attributes["A"] = [2000, 200, 100]; Attributes["B"] = [3000, 0, 0];
        writes = originalCalls = 0; failRead = false; progression = 0;
        char* name = stackalloc char[2] { 'A', '\0' };
        byte* entries = stackalloc byte[3 * 16];
        SetEntry(entries, 0, 74, 99999); // Deliberate sparse hole.
        SetEntry(entries, 1, 74, 1000);
        SetEntry(entries, 2, 45, 99999); // Unrelated attribute must never be written.
        RolePropertyChange change = new()
        {
            Role = new() { Data = name, Length = 2, Capacity = 2 }, AddedFavor = 3, SpentStamina = 4,
            Attributes = new() { Elements = entries, SlotCount = 3, SlotCapacity = 3, AllocationBitCount = 3 }
        };
        change.Attributes.InlineAllocationFlags[0] = 6;
        RolePropertyChanges changes = new() { Data = &change, Length = 1, Capacity = 1 };
        byte[] before = new ReadOnlySpan<byte>(&change, sizeof(RolePropertyChange)).ToArray();
        Game().HandleRolePropertyChanges(7, &changes);
        Check.Equal(1100, Attributes["A"][2]); Check.Equal(200, Attributes["A"][1]);
        Check.Equal(0, originalCalls); Check.Equal(1, writes);
        Check.True(new ReadOnlySpan<byte>(&change, sizeof(RolePropertyChange)).SequenceEqual(before), "Borrowed request mutated.");

        // A synthetic disk-backed native API demonstrates that a new adapter uses stored values, not a C# volume cache.
        Attributes.Clear(); Reload();
        SetEntry(entries, 1, 73, 1000);
        Game().HandleRolePropertyChanges(0, &changes);
        Check.Equal(900, Attributes["A"][1]); Check.Equal(1100, Attributes["A"][2]);
        Check.Equal(2, writes);
        Game().HandleRolePropertyChanges(7, &changes);
        Check.Equal(2, writes); // Already at shared capacity.
        SetEntry(entries, 1, 74, 500);
        Game().HandleRolePropertyChanges(99, &changes);
        Check.Equal(2, writes); Check.Equal(0, Attributes["B"][2]);
        name[0] = 'B';
        Game().HandleRolePropertyChanges(7, &changes);
        Check.Equal(500, Attributes["B"][2]); Check.Equal(1100, Attributes["A"][2]);
        name[0] = 'A';

        // Simulate the existing native clear routine's two attribute writes, then another experiment.
        ((delegate* unmanaged<GameString*, int, int, void>)&Write)(&change.Role, 73, 0);
        ((delegate* unmanaged<GameString*, int, int, void>)&Write)(&change.Role, 74, 0);
        Attributes.Clear(); Reload(); progression = 1;
        Game().HandleRolePropertyChanges(7, &changes);
        Check.Equal(500, Attributes["A"][2]); Check.Equal(1, originalCalls);
        Game().HandleRolePropertyChanges(7, &changes);
        Check.Equal(1000, Attributes["A"][2]); Check.Equal(2, originalCalls); // Identical legitimate shots accumulate.
        failRead = true;
        nint changesAddress = (nint)(&changes);
        try { Check.Throws<InvalidOperationException>(() => ThrowingRequest(Game(), changesAddress)); }
        finally { failRead = false; }
        Check.Equal(3, originalCalls); // Original behavior survives a managed persistence failure.
        changes.Length = 2; // Invalid array header must not be traversed by the new adapter.
        int oldWrites = writes;
        Game().HandleRolePropertyChanges(7, &changes);
        Check.Equal(oldWrites, writes); Check.Equal(3, originalCalls); // Reject malformed headers before either stamina or native traversal.
    }

    private static void ThrowingRequest(NativeGame game, nint changes) => game.HandleRolePropertyChanges(7, (RolePropertyChanges*)changes);

    private static void VerifyMissingCapacity()
    {
        using var temporary = new TemporaryWorkspace();
        storeFile = Path.Combine(temporary.Paths.Root, "synthetic-attributes.txt");
        Attributes.Clear(); Attributes["A"] = [0, 0, 0];
        writes = 0; progression = 0; fixedCapacity = true;
        try
        {
            char* name = stackalloc char[2] { 'A', '\0' };
            byte* entries = stackalloc byte[16];
            SetEntry(entries, 0, 74, 984);
            RolePropertyChange change = new()
            {
                Role = new() { Data = name, Length = 2, Capacity = 2 },
                Attributes = new() { Elements = entries, SlotCount = 1, SlotCapacity = 1, AllocationBitCount = 1 }
            };
            change.Attributes.InlineAllocationFlags[0] = 1;
            RolePropertyChanges changes = new() { Data = &change, Length = 1, Capacity = 1 };
            Game().HandleRolePropertyChanges(7, &changes);
            Check.Equal(984, Attributes["A"][2]);
            Attributes.Clear(); Reload();
            int capacity = ReadCapacity(&change.Role);
            Check.Equal(1000, capacity);
            Check.Equal(0.984, (Attributes["A"][1] + Attributes["A"][2]) / (double)capacity);
            Check.Equal(16, capacity - Attributes["A"][1] - Attributes["A"][2]);

            Attributes["A"][0] = 9000; // Override even a conflicting stored value, matching the newer build.
            SetEntry(entries, 0, 73, 500);
            Game().HandleRolePropertyChanges(7, &changes);
            Check.Equal(16, Attributes["A"][1]); Check.Equal(984, Attributes["A"][2]);
            // The existing drain writes only current volumes. Capacity remains available afterwards.
            ((delegate* unmanaged<GameString*, int, int, void>)&Write)(&change.Role, 73, 0);
            ((delegate* unmanaged<GameString*, int, int, void>)&Write)(&change.Role, 74, 0);
            Attributes.Clear(); Reload();
            Check.Equal(1000, ReadCapacity(&change.Role));
            Game().HandleRolePropertyChanges(7, &changes);
            Check.Equal(500, Attributes["A"][1]); Check.Equal(0, Attributes["A"][2]);
        }
        finally { fixedCapacity = false; }
    }

    private static readonly (string Role, int Capacity)[] CharacterCapacities =
    [
        ("Erika", 3300), ("Galatea", 2100), ("Talon", 3600), ("Alet", 2600), ("Anya", 4200),
        ("Celia", 3500), ("Juzi", 3400), ("Elizabeth", 3600), ("YanShi", 2500), ("ADA", 4300)
    ];

    internal static void VerifyCapacity()
    {
        VerifyCharacterCapacities();
        if (Environment.GetEnvironmentVariable("DOLLHOUSE_TEST_GAMEPLAY") is { } path)
        {
            nint library = NativeLibrary.Load(Path.GetFullPath(path)); // Only our compiled runtime.
            try { VerifyNativeCapacity(library); }
            finally { NativeLibrary.Free(library); }
        }
    }

    private static void VerifyNativeCapacity(nint library)
    {
        var read = (delegate* unmanaged<GameString*, int>)NativeLibrary.GetExport(library, "DollhouseSemenCapacity");
        Check.Equal(1000, read(null));
        foreach (var (role, capacity) in CharacterCapacities)
            fixed (char* chars = role)
            {
                GameString value = new() { Data = chars, Length = role.Length + 1, Capacity = role.Length + 1 };
                Check.Equal(capacity, read(&value));
            }
    }

    private static void VerifyCharacterCapacities()
    {
        Check.Equal(1000, SemenCapacity.ForRole("Unknown"));
        Check.Equal(1000, SemenCapacity.ForRole(""));
        Check.Equal(4300, SemenCapacity.ForRole("Ada"));
        Check.Equal(2500, SemenCapacity.ForRole("Yanshi"));
        Check.Equal(1000, SemenCapacity.Read(null));
        GameString invalid = default;
        Check.Equal(1000, SemenCapacity.Read(&invalid));
        using var temporary = new TemporaryWorkspace();
        storeFile = Path.Combine(temporary.Paths.Root, "synthetic-character-capacities.txt");
        fixedCapacity = true; progression = 0;
        try
        {
            byte* entries = stackalloc byte[16];
            foreach (var (role, capacity) in CharacterCapacities)
            {
                Check.Equal(capacity, SemenCapacity.ForRole(role));
                Attributes.Clear(); Attributes[role] = [9999, 0, 0]; // Conflicting saved capacity stays untouched.
                fixed (char* chars = role)
                {
                    RolePropertyChange change = new()
                    {
                        Role = new() { Data = chars, Length = role.Length + 1, Capacity = role.Length + 1 },
                        Attributes = new() { Elements = entries, SlotCount = 1, SlotCapacity = 1, AllocationBitCount = 1 }
                    };
                    change.Attributes.InlineAllocationFlags[0] = 1;
                    RolePropertyChanges changes = new() { Data = &change, Length = 1, Capacity = 1 };
                    SetEntry(entries, 0, 73, capacity + 100);
                    Game().HandleRolePropertyChanges(7, &changes);
                    Attributes.Clear(); Reload();
                    Check.Equal(capacity, Attributes[role][1]); Check.Equal(0, Attributes[role][2]);
                    Check.Equal(capacity, ReadCapacity(&change.Role));
                    Check.Equal(1d, Attributes[role][1] / (double)ReadCapacity(&change.Role)); // UI fullness.
                    Check.Equal(0, ReadCapacity(&change.Role) - Attributes[role][1]); // Native remaining-space calculation.
                    ((delegate* unmanaged<GameString*, int, int, void>)&Write)(&change.Role, 73, 0);
                    ((delegate* unmanaged<GameString*, int, int, void>)&Write)(&change.Role, 74, 0);
                    Attributes.Clear(); Reload();
                    Check.Equal(capacity, ReadCapacity(&change.Role)); // Draining does not alter capacity.
                    SetEntry(entries, 0, 74, capacity + 100);
                    Game().HandleRolePropertyChanges(7, &changes);
                    Check.Equal(capacity, Attributes[role][2]); Check.Equal(0, Attributes[role][1]);
                    Check.Equal(9999, Attributes[role][0]);
                }
            }
        }
        finally { fixedCapacity = false; }
    }

    private static int ReadCapacity(GameString* role) =>
        ((delegate* unmanaged<GameString*, int, int>)&Read)(role, CumflationPatch.CapacityAttribute);
    private static string Role(GameString* role) => new(role->Data, 0, role->Length - 1);
    [UnmanagedCallersOnly] private static void ReadStamina(GameString* role, int* current, int* maximum)
    { *current = Stamina.GetValueOrDefault(new string(role->Data), 20); *maximum = 20; }
    [UnmanagedCallersOnly] private static void SpendStamina(GameString* role, int cost, char* reason)
    {
        string name = new(role->Data);
        Stamina[name] = Math.Clamp(Stamina.GetValueOrDefault(name, 20) - cost, 0, 20);
    }
    [UnmanagedCallersOnly] private static void FatigueLog(char* message) { }
    [UnmanagedCallersOnly] private static int Player() => 7;
    [UnmanagedCallersOnly] private static byte Mode() => progression;
    [UnmanagedCallersOnly]
    private static void Original(int player, RolePropertyChanges* changes)
    {
        if (player != 7 || changes->Data->AddedFavor != 3 || changes->Data->SpentStamina != 4)
            Environment.FailFast("Original-handler ABI or request payload changed.");
        originalCalls++;
    }
    [UnmanagedCallersOnly] private static int Read(GameString* role, int attribute) =>
        fixedCapacity && attribute == CumflationPatch.CapacityAttribute ? SemenCapacity.Read(role) : Attributes[Role(role)][attribute - 72];
    [UnmanagedCallersOnly]
    private static void Write(GameString* role, int attribute, int value)
    {
        if (attribute is not (73 or 74)) Environment.FailFast("Unrelated role attribute modified.");
        Attributes[Role(role)][attribute - 72] = value; writes++;
        File.WriteAllLines(storeFile, Attributes.SelectMany(pair => pair.Value.Select((amount, index) => $"{pair.Key}\t{index}\t{amount}")));
    }
    private static void Reload()
    {
        foreach (var line in File.ReadAllLines(storeFile))
        {
            var fields = line.Split('\t');
            if (!Attributes.TryGetValue(fields[0], out var values)) Attributes[fields[0]] = values = new int[3];
            values[int.Parse(fields[1])] = int.Parse(fields[2]);
        }
    }

    private static int Offset(PEHeaders headers, int rva)
    {
        var section = headers.SectionHeaders.Single(s => rva >= s.VirtualAddress && rva < s.VirtualAddress + s.SizeOfRawData);
        return section.PointerToRawData + rva - section.VirtualAddress;
    }

    private static void VerifyNativeEvidence()
    {
        if (GameBinaryFixture.Original is not { } fixture) return;
        var original = File.ReadAllBytes(fixture);
        using var pe = new PEReader(new MemoryStream(original));
        VerifyWorldContextEvidence(original, pe.PEHeaders);
    }
    private static void VerifyWorldContextEvidence(byte[] original, PEHeaders headers)
    {
        Instruction At(int rva, int length) => Decoder.Create(64,
            new ByteArrayCodeReader(original.AsSpan(Offset(headers, rva), length).ToArray()), (ulong)rva).Decode();
        // This is the special action singleton factory, not the ordinary PlayerClientInstance
        // proxy registry. It obtains /Engine/Transient before constructing the action proxy.
        var packageCall = At(0x53e85b8, 5);
        Check.Equal(Mnemonic.Call, packageCall.Mnemonic); Check.Equal(0x153d4a0UL, packageCall.NearBranch64);
        var package = At(0x153d4a0, 7);
        Check.Equal(Mnemonic.Mov, package.Mnemonic); Check.True(package.IsIPRelativeMemoryOperand, "Transient-package source changed.");
        // The active-world address is derived from the engine's own world-context resolver.
        var world = At(0x45259f9, 7);
        Check.Equal(Mnemonic.Mov, world.Mnemonic); Check.True(world.IsIPRelativeMemoryOperand, "World source changed.");
        Check.Equal((ulong)SupportedBuild.CurrentWorld, world.IPRelativeMemoryAddress);
        Check.True(package.IPRelativeMemoryAddress != world.IPRelativeMemoryAddress, "Action owner was mistaken for the active world.");
        var ticker = At(SupportedBuild.FrameTickerCall, 5);
        Check.Equal(Mnemonic.Call, ticker.Mnemonic);
        Check.Equal((ulong)SupportedBuild.FrameTickerTick, ticker.NearBranch64);
        var tickerDelta = At(0x4594ba2, 4);
        Check.Equal(Mnemonic.Cvtpd2ps, tickerDelta.Mnemonic);
        Check.Equal(Register.XMM1, tickerDelta.Op0Register);
        var serialCheck = At(SupportedBuild.GetWeakObject + 0x38, 4);
        Check.Equal(Mnemonic.Cmp, serialCheck.Mnemonic);
        Check.Equal(0x10UL, serialCheck.MemoryDisplacement64);
        var metadata = At(SupportedBuild.DiscardScriptMetadataCall, 5);
        Check.Equal(Mnemonic.Call, metadata.Mnemonic);
        Check.Equal((ulong)SupportedBuild.DiscardScriptMetadata, metadata.NearBranch64);
        var developmentMode = At(0x51537d8, 7);
        Check.Equal(0xaff0240UL, developmentMode.IPRelativeMemoryAddress);
        var skipMetadata = At(0x51537df, 2);
        Check.Equal(Mnemonic.Jne, skipMetadata.Mnemonic);
        Check.Equal((ulong)SupportedBuild.DiscardScriptMetadataCall + 5, skipMetadata.NearBranch64);
        var clearTypeCount = At(0x533ecf6, 7);
        Check.Equal(Mnemonic.Mov, clearTypeCount.Mnemonic);
        Check.Equal(0x2c8UL, clearTypeCount.MemoryDisplacement64);
        Check.Equal(Register.R12D, clearTypeCount.Op1Register);
        var destroyTemporaryData = At(0x5153805, 5);
        Check.Equal(0x5338ab0UL, destroyTemporaryData.NearBranch64);
        // Concrete implementations come from the shipping fork's own vtables/call adapters.
        // Upstream interface indices are not a contract for this binary.
        void Vtable(int table, int slot, int method) => Check.Equal(0x140000000UL + (uint)method,
            BitConverter.ToUInt64(original, Offset(headers, table) + slot));
        Vtable(0x94d7168, 0, SupportedBuild.ScriptModuleEngine);
        Vtable(0x94d7168, 0xc8, SupportedBuild.ScriptModuleFindType);
        Vtable(0x94d7168, 0x58, SupportedBuild.ScriptModuleFunctionCount);
        Vtable(0x94d7168, 0x60, SupportedBuild.ScriptModuleFunction);
        Vtable(0x94d68c8, 0x78, SupportedBuild.ScriptFunctionNamespace);
        Vtable(0x94d75a8, 0x98, SupportedBuild.ScriptSetArgAddress);
        Vtable(0x94d75a8, 0x68, SupportedBuild.ScriptSetArgByte);
        Vtable(0x94d75a8, 0x90, SupportedBuild.ScriptSetArgDouble);
        foreach (var (site, target) in new[] { (SupportedBuild.CaptureDrainWrite, SupportedBuild.WriteRoleAttribute),
            (SupportedBuild.DrainClearedBroadcast, SupportedBuild.Broadcast),
            (SupportedBuild.DrainRightDirectCall, SupportedBuild.ShowRightMsg), (SupportedBuild.DrainRightExecCall, SupportedBuild.ShowRightMsg) })
        {
            var call = At(site, 5); Check.Equal(Mnemonic.Call, call.Mnemonic); Check.Equal((ulong)target, call.NearBranch64);
        }
        var allocate = At(SupportedBuild.Allocate, 5);
        Check.Equal(Mnemonic.Mov, allocate.Mnemonic); Check.Equal(Register.RBX, allocate.Op1Register);
        Check.Equal(0x10UL, allocate.MemoryDisplacement64);
        var allocatorCall = At(0x11fdb15, 3);
        Check.Equal(Mnemonic.Call, allocatorCall.Mnemonic); Check.Equal(0x28UL, allocatorCall.MemoryDisplacement64);
        Vtable(0x94d72d8, 0x168, SupportedBuild.ScriptGetModule);
        Vtable(0x94d6ff0, 0, SupportedBuild.ScriptTypeEngine);
        Vtable(0x94d6ff0, 0x30, SupportedBuild.ScriptTypeName);
        Vtable(0x94d6ff0, 0x60, SupportedBuild.ScriptTypeSize);
        // Query ABI uses UClass**; visibility reads the live Slate widget and releases
        // its shared reference. These are engine APIs, not baked UI field offsets.
        var widgetClass = At(SupportedBuild.AllWidgetsOfClass + 0x47, 3);
        Check.Equal(Mnemonic.Mov, widgetClass.Mnemonic);
        Check.Equal(Register.RDI, widgetClass.Op0Register); Check.Equal(Register.R15, widgetClass.MemoryBase);
        var visibilityWidget = At(SupportedBuild.WidgetVisible + 0xb, 5);
        Check.Equal(Mnemonic.Call, visibilityWidget.Mnemonic); Check.Equal(0x36a1c50UL, visibilityWidget.NearBranch64);
        var visibleMask = At(SupportedBuild.WidgetVisible + 0x1a, 4);
        Check.Equal(Mnemonic.Test, visibleMask.Mnemonic); Check.Equal(0x19UL, visibleMask.Immediate8);
        var releaseWidget = At(SupportedBuild.WidgetVisible + 0x26, 5);
        Check.Equal(Mnemonic.Call, releaseWidget.Mnemonic); Check.Equal(0x12012c0UL, releaseWidget.NearBranch64);
        Vtable(0x94d6ff0, 0x68, SupportedBuild.ScriptTypeId);
        Vtable(0x94d6ff0, 0x70, SupportedBuild.ScriptSubtypeId);
        Vtable(0x94d6ff0, 0x78, SupportedBuild.ScriptSubtype);
        Vtable(0x94d72d8, 0x1a8, SupportedBuild.ScriptTypeById);
        Vtable(0x94d75a8, 0xf0, SupportedBuild.ScriptReturnObject);
        var meshGetter = At(SupportedBuild.MeshAnimInstance, 7);
        Check.Equal(Mnemonic.Mov, meshGetter.Mnemonic);
        Check.Equal(Register.RAX, meshGetter.Op0Register);
        Check.Equal(Register.RCX, meshGetter.MemoryBase);
        Check.Equal(0x918UL, meshGetter.MemoryDisplacement64);
        var meshExecGetter = At(0x3bcc815, 7);
        Check.Equal(meshGetter.Code, meshExecGetter.Code);
        Check.Equal(meshGetter.MemoryDisplacement64, meshExecGetter.MemoryDisplacement64);
        Vtable(0x94d6ff0, 0xc8, SupportedBuild.ScriptMethodByName);
        Vtable(0x94d6ff0, 0xd8, SupportedBuild.ScriptPropertyCount);
        Vtable(0x94d6ff0, 0xe0, SupportedBuild.ScriptProperty);
        Vtable(0x94d6ff0, 0x148, SupportedBuild.ScriptTypeNativeClass);
        Vtable(0x94d68c8, 0x68, SupportedBuild.ScriptFunctionObjectName);
        Vtable(0x94d68c8, 0x70, SupportedBuild.ScriptFunctionName);
        Vtable(0x94d68c8, 0xc0, SupportedBuild.ScriptParameterCount);
        Vtable(0x94d68c8, 0xc8, SupportedBuild.ScriptParameter);
        Vtable(0x94d68c8, 0xd0, SupportedBuild.ScriptReturnType);
        Vtable(0x94d75a8, 0x10, SupportedBuild.ScriptGetEngine);
        Vtable(0x94d75a8, 0x18, SupportedBuild.ScriptPrepare);
        Vtable(0x94d75a8, 0x28, SupportedBuild.ScriptExecute);
        Vtable(0x94d75a8, 0x60, SupportedBuild.ScriptSetObject);
        Vtable(0x94d75a8, 0x108, SupportedBuild.ScriptExceptionLine);
        Vtable(0x94d75a8, 0x118, SupportedBuild.ScriptExceptionString);
        var receiverCall = At(0x52e0344, 5);
        Check.Equal(Mnemonic.Call, receiverCall.Mnemonic);
        Check.Equal((ulong)SupportedBuild.ScriptSetObject, receiverCall.NearBranch64);
        var objectClass = At(0x538c331, 4);
        Check.Equal((ulong)SupportedBuild.ObjectClass, objectClass.MemoryDisplacement64);
        var ancestorDepth = At(0x538c339, 4);
        Check.Equal((ulong)SupportedBuild.ClassDepth, ancestorDepth.MemoryDisplacement64);
        var ancestorArray = At(0x538c345, 4);
        Check.Equal((ulong)SupportedBuild.ClassAncestors, ancestorArray.MemoryDisplacement64);
        var nativeClassSlot = At(0x509cb92, 6);
        Check.Equal(Mnemonic.Call, nativeClassSlot.Mnemonic);
        Check.Equal(0x148UL, nativeClassSlot.MemoryDisplacement64);
        var nativeClassUse = At(0x509cbb6, 4);
        Check.Equal(Mnemonic.Mov, nativeClassUse.Mnemonic);
        Check.Equal(0x78UL, nativeClassUse.MemoryDisplacement64);
        var properties = At(SupportedBuild.ScriptProperty + 0xc, 6);
        Check.Equal(0x98UL, properties.MemoryDisplacement64);
        var scopeCreate = At(0x52e02f7, 5);
        Check.Equal(Mnemonic.Call, scopeCreate.Mnemonic);
        Check.Equal((ulong)SupportedBuild.ScriptScopeCreate, scopeCreate.NearBranch64);
        var scopeRelease = At(0x52e0362, 5);
        Check.Equal(Mnemonic.Call, scopeRelease.Mnemonic);
        Check.Equal((ulong)SupportedBuild.ScriptScopeRelease, scopeRelease.NearBranch64);
        var scriptWorld = At(0x52e031f, 7);
        Check.Equal(Mnemonic.Mov, scriptWorld.Mnemonic);
        Check.Equal((ulong)SupportedBuild.ScriptWorldContext, scriptWorld.IPRelativeMemoryAddress);
    }

}
