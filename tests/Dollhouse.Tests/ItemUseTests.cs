using System.Buffers.Binary;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text;
using Dollhouse.App;
using Dollhouse.Core.Patching;
using Dollhouse.Core.Startup;
using Dollhouse.Gameplay;
using SupportedBuild = Dollhouse.Gameplay.SupportedBuild;

namespace Dollhouse.Tests;

internal static unsafe class ItemUseTests
{
    public static void Verify()
    {
        VerifyPolicy();
        VerifyRepairPolicy();
        RepairGelFixture.Verify();
        VerifyNativeBindings();
        Check.Throws<ArgumentException>(() => AppOptions.Parse(["--game-patch", "aphrodisiac-effects"]));
        Check.Throws<ArgumentException>(() => AppOptions.Parse(["--game-patch", "local-progression"]));
        Check.Throws<ArgumentException>(() => AppOptions.Parse(["--clean", "--game-patch", "aphrodisiac-effects"]));
        Check.Throws<ArgumentException>(() => AppOptions.Parse(["--game-patch", "aphrodisiac-effects", "--game-patch", "aphrodisiac-effects"]));
        if (Environment.GetEnvironmentVariable("DOLLHOUSE_TEST_GAMEPLAY") is { } path)
        {
            // Our compiled library only. Retain the Native AOT handle for process lifetime.
            nint library = NativeLibrary.Load(Path.GetFullPath(path));
            foreach (var name in GameplayRuntimePatch.Plan.ExportNames) Check.True(NativeLibrary.GetExport(library, name) != 0, "Missing gameplay export.");
            ((delegate* unmanaged<nint, nint, void>)NativeLibrary.GetExport(library, "DollhouseUseItem"))(0, 0);
            ((delegate* unmanaged<nint, nint*, void>)NativeLibrary.GetExport(library, "DollhouseReflectedUseItem"))(0, null);
            ((delegate* unmanaged<nint, nint, int, void>)NativeLibrary.GetExport(library, "DollhouseItemUseCompleted"))(0, 0, 0);
            VerifyExportTable(path);
        }
    }

    private sealed class FakeGame : IItemUseGame
    {
        public bool IsFullUnlock { get; set; }
        public int Item { get; set; } = ItemUsePatch.Aphrodisiac;
        public int Opening { get; set; }
        public bool ResetSucceeds { get; set; } = true;
        public bool CompleteNativeUse { get; set; }
        public int Consumed;
        public bool Role { get; set; } = true;
        public nint Roles { get; set; } = 3;
        public long Stock { get; set; }
        public int Reads;
        public readonly List<string> Events = [];
        public readonly List<string> Failures = [];
        public int ItemId(nint request) => Item;
        public int RecoveryType(nint request) => Opening;
        public bool HasRole(nint request) => Role;
        public long InventoryCount(int itemId) { Reads++; return Stock; }
        public void HandleLocalItemUse(nint bag, nint request)
        {
            Events.Add("native-validation");
            if (!CompleteNativeUse || ItemUsePatch.AvailableStock(this, Item) < 1) return;
            Consumed++;
            ItemUsePatch.CompleteSuccessfulUse(this, bag, request);
        }
        public nint FindRolesProxy(nint bag) => Roles;
        public void NotifyRoleItemUse(nint roles, nint request) => Events.Add("role-effect");
        public bool ResetDilation(nint roles, nint request) { Events.Add("reset:" + Opening); return ResetSucceeds; }
        public bool RecoverStamina(nint request) { Events.Add("reset:2"); return ResetSucceeds; }
        public void ItemUseFailed(string message) => Failures.Add(message);
        public void AddAffinity(nint bag, nint request, int amount) => Events.Add("affinity:" + amount);
        public void NotifyItemUseUI(nint bag, nint request) => Events.Add("ui");
    }

    private static void VerifyRepairPolicy()
    {
        foreach (bool unlock in new[] { false, true })
        foreach (int opening in new[] { 0, 1, 2 })
        {
            var game = new FakeGame { Item = ItemUsePatch.RepairGel, Opening = opening, IsFullUnlock = unlock, Stock = 1, CompleteNativeUse = true };
            Check.Equal(unlock ? 999L : 1L, ItemUsePatch.AvailableStock(game, ItemUsePatch.RepairGel));
            ItemUsePatch.UseItem(game, 1, 2);
            Check.Equal(1, game.Consumed);
            Check.True(game.Events.SequenceEqual(new[] { "native-validation", "reset:" + opening, "affinity:3", "ui" }), "Repair effect/affinity/UI sequence changed.");
            Check.Equal(0, game.Failures.Count);
        }
        foreach (int opening in new[] { -1, 3, int.MaxValue })
        {
            var game = new FakeGame { Item = ItemUsePatch.RepairGel, Opening = opening, CompleteNativeUse = true, Stock = 1 };
            ItemUsePatch.UseItem(game, 1, 2);
            Check.Equal(0, game.Consumed); Check.Equal(0, game.Events.Count);
            Check.True(game.Failures.Single().Contains("RecoverType=" + opening), "Missing selector failure log.");
            ItemUsePatch.CompleteSuccessfulUse(game, 1, 2);
            Check.Equal(0, game.Events.Count); // Defense at the post-validation callback too.
        }
        foreach (var game in new[] {
            new FakeGame { Item = ItemUsePatch.RepairGel, Role = false },
            new FakeGame { Item = ItemUsePatch.RepairGel, Roles = 0 } })
        {
            ItemUsePatch.UseItem(game, 1, 2);
            Check.Equal(0, game.Events.Count); Check.Equal(1, game.Failures.Count);
            ItemUsePatch.CompleteSuccessfulUse(game, 1, 2); Check.Equal(0, game.Events.Count);
        }
        var emptyStock = new FakeGame { Item = ItemUsePatch.RepairGel, Stock = 0, CompleteNativeUse = true };
        ItemUsePatch.UseItem(emptyStock, 1, 2);
        Check.Equal(0, emptyStock.Consumed); Check.True(emptyStock.Events.SequenceEqual(new[] { "native-validation" }), "Insufficient stock reached the effect.");
        var failed = new FakeGame { Item = ItemUsePatch.RepairGel, ResetSucceeds = false, Stock = 1, CompleteNativeUse = true };
        ItemUsePatch.UseItem(failed, 1, 2);
        Check.True(failed.Events.SequenceEqual(new[] { "native-validation", "reset:0" }), "Failed reset granted affinity or UI success.");
    }

    private static void VerifyPolicy()
    {
        foreach (bool fullUnlock in new[] { false, true })
        {
            var game = new FakeGame { IsFullUnlock = fullUnlock, Stock = 7 };
            ItemUsePatch.UseItem(game, 1, 2);
            Check.True(game.Events.SequenceEqual(new[] { "native-validation" }), "Request must retain native validation and not apply an effect early.");
            game.Events.Clear();
            Check.Equal(fullUnlock ? 999L : 7L, ItemUsePatch.AvailableStock(game, ItemUsePatch.Aphrodisiac));
            Check.Equal(fullUnlock ? 0 : 1, game.Reads);
            Check.Equal(7L, ItemUsePatch.AvailableStock(game, 123));
            ItemUsePatch.CompleteSuccessfulUse(game, 1, 2);
            Check.True(game.Events.SequenceEqual(new[] { "role-effect", "affinity:3", "ui" }), "Completion order changed.");
        }
        foreach (var game in new[] { new FakeGame { Item = 123 }, new FakeGame { Role = false }, new FakeGame { Roles = 0 } })
        {
            ItemUsePatch.CompleteSuccessfulUse(game, 1, 2);
            Check.Equal(0, game.Events.Count);
        }
        var empty = new FakeGame();
        ItemUsePatch.UseItem(empty, 0, 2);
        ItemUsePatch.UseItem(empty, 1, 0);
        ItemUsePatch.CompleteSuccessfulUse(empty, 0, 2);
        ItemUsePatch.CompleteSuccessfulUse(empty, 1, 0);
        Check.Equal(0, empty.Events.Count);
    }

    private static readonly List<string> NativeEvents = [];
    private static int copies, frees;
    private static byte mode;
    private static nint bagAddress, rolesAddress;
    private static bool nativeValid;

    private static void VerifyNativeBindings()
    {
        Check.Equal(16, sizeof(GameString));
        Check.Equal(32, sizeof(ItemRequest));
        Check.Equal(24, sizeof(RoleUseEvent));
        Check.Equal(16, Marshal.OffsetOf<ItemRequest>(nameof(ItemRequest.ItemId)).ToInt32());
        Check.Equal(24, Marshal.OffsetOf<ItemRequest>(nameof(ItemRequest.RecoverType)).ToInt32());
        Check.Equal(28, Marshal.OffsetOf<ItemRequest>(nameof(ItemRequest.Reserved)).ToInt32());
        Check.Equal(8, Marshal.OffsetOf<RoleUseEvent>(nameof(RoleUseEvent.Role)).ToInt32());
        NativeEvents.Clear(); copies = frees = 0; nativeValid = true;
        bagAddress = 0x1110; rolesAddress = 0x2220;
        var game = new NativeGame(0, rva => rva switch
        {
            SupportedBuild.ProfileMode => (nint)(delegate* unmanaged<byte>)&Mode,
            SupportedBuild.InventoryCount => (nint)(delegate* unmanaged<int, long>)&Count,
            SupportedBuild.LocalItemUse => (nint)(delegate* unmanaged<nint, nint, void>)&LocalUse,
            SupportedBuild.RolesClass => (nint)(delegate* unmanaged<nint>)&Class,
            SupportedBuild.FindProxy => (nint)(delegate* unmanaged<nint, nint, nint>)&Proxy,
            SupportedBuild.CopyString => (nint)(delegate* unmanaged<GameString*, GameString*, nint>)&Copy,
            SupportedBuild.Free => (nint)(delegate* unmanaged<void*, void>)&Free,
            SupportedBuild.Broadcast => (nint)(delegate* unmanaged<nint, void*, void>)&Broadcast,
            SupportedBuild.AddAffinity => (nint)(delegate* unmanaged<nint, nint, int, byte, nint, void>)&Affinity,
            SupportedBuild.UseItemReason => 0x3330,
            _ => throw new InvalidOperationException("Unexpected native call")
        });
        fixed (char* role = "SyntheticRole")
        {
            ItemRequest request = new() { Role = new() { Data = role, Length = 14, Capacity = 14 }, ItemId = 99982, Count = 2, RecoverType = 17 };
            mode = 0;
            Check.Equal(999L, ItemUsePatch.AvailableStock(game, 99982));
            mode = 1;
            Check.Equal(4L, ItemUsePatch.AvailableStock(game, 99982));
            ItemUsePatch.UseItem(game, bagAddress, (nint)(&request));
            ItemUsePatch.CompleteSuccessfulUse(game, bagAddress, (nint)(&request));
            Check.True(request.Role.Data == role && request.Count == 2 && request.RecoverType == 17, "Borrowed request changed.");
        }
        Check.True(nativeValid, "Native arguments/layouts differed from the bridge ABI.");
        Check.Equal(2, copies); Check.Equal(2, frees);
        Check.True(NativeEvents.SequenceEqual(new[] { "native", "role", "affinity", "ui" }), "Native bridge order changed.");
    }

    [UnmanagedCallersOnly] private static byte Mode() => mode;
    [UnmanagedCallersOnly] private static long Count(int id) { nativeValid &= id == 99982; return 4; }
    [UnmanagedCallersOnly] private static void LocalUse(nint bag, nint request) { nativeValid &= bag == bagAddress && ((ItemRequest*)request)->ItemId == 99982; NativeEvents.Add("native"); }
    [UnmanagedCallersOnly] private static nint Class() => 0x4440;
    [UnmanagedCallersOnly] private static nint Proxy(nint bag, nint type) { nativeValid &= bag == bagAddress && type == 0x4440; return rolesAddress; }
    [UnmanagedCallersOnly] private static nint Copy(GameString* to, GameString* from)
    {
        *to = *from;
        to->Data = (char*)Marshal.AllocHGlobal(from->Length * 2);
        new ReadOnlySpan<char>(from->Data, from->Length).CopyTo(new Span<char>(to->Data, to->Length));
        copies++; return (nint)to;
    }
    [UnmanagedCallersOnly] private static void Free(void* value) { Marshal.FreeHGlobal((nint)value); frees++; }
    [UnmanagedCallersOnly] private static void Broadcast(nint address, void* value)
    {
        if (address == rolesAddress + SupportedBuild.RolesItemUseDelegate)
        {
            var e = (RoleUseEvent*)value;
            nativeValid &= e->Result == 0 && e->ItemId == 99982 && new string(e->Role.Data) == "SyntheticRole";
            NativeEvents.Add("role");
        }
        else
        {
            var e = (ItemRequest*)value;
            nativeValid &= address == bagAddress + SupportedBuild.BagItemUseUIDelegate && e->ItemId == 99982 && e->Count == 2 && e->RecoverType == 17;
            NativeEvents.Add("ui");
        }
    }
    [UnmanagedCallersOnly] private static void Affinity(nint bag, nint request, int amount, byte enabled, nint reason)
    {
        nativeValid &= bag == bagAddress && ((ItemRequest*)request)->ItemId == 99982 && amount == 3 && enabled == 1 && reason == 0x3330;
        NativeEvents.Add("affinity");
    }

    private static int Offset(PEHeaders headers, int rva)
    {
        var section = headers.SectionHeaders.Single(s => rva >= s.VirtualAddress && rva < s.VirtualAddress + s.SizeOfRawData);
        return section.PointerToRawData + rva - section.VirtualAddress;
    }
    private static string StringAt(byte[] bytes, int offset) => Encoding.ASCII.GetString(bytes, offset, Array.IndexOf(bytes, (byte)0, offset) - offset);

    private static void VerifyExportTable(string path)
    {
        var bytes = File.ReadAllBytes(path);
        using var reader = new PEReader(new MemoryStream(bytes));
        var headers = reader.PEHeaders;
        int exports = Offset(headers, headers.PEHeader!.ExportTableDirectory.RelativeVirtualAddress);
        int count = BitConverter.ToInt32(bytes, exports + 24);
        int names = Offset(headers, BitConverter.ToInt32(bytes, exports + 32));
        var actual = Enumerable.Range(0, count).Select(i => StringAt(bytes, Offset(headers, BitConverter.ToInt32(bytes, names + i * 4)))).ToArray();
        Check.True(GameplayRuntimePatch.Plan.ExportNames.All(actual.Contains), "Native DLL exports don't match installed imports.");
        Check.True(!actual.Contains("DollhousePersistCumflation"), "Retired context-free cumflation export is still published.");
    }

}
