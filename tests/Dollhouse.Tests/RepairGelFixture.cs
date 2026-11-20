using System.Runtime.InteropServices;
using Dollhouse.Gameplay;

namespace Dollhouse.Tests;

// Calls only our managed adapter and analyst-owned native stubs, never game code.
internal sealed unsafe class RepairGelFixture
{
    private const nint Bag = 0x1000, Roles = 0x2000, RoleClass = 0x3000, Reason = 0x4000;
    private static RepairGelFixture Current = null!;
    private readonly int[] dilation = [24567, 37890];
    private readonly List<string> events = [], logs = [];
    private readonly NativeGame game;
    private nint request;
    private bool valid = true, unlock, rejectWrite, ignoreWrite, missingProxy;
    private string failureStage = "";
    private int stock = 1, consumed, copies, frees;
    private int stamina = 12;

    private RepairGelFixture()
    {
        Current = this;
        game = new(0, rva =>
        {
            if ((rva == SupportedBuild.ReadRoleDilation && failureStage == "read") ||
                (rva == SupportedBuild.AddRoleDilation && failureStage == "native reset"))
                throw new InvalidOperationException("Synthetic native binding unavailable.");
            return rva switch
            {
                SupportedBuild.ProfileMode => (nint)(delegate* unmanaged<byte>)&Mode,
                SupportedBuild.InventoryCount => (nint)(delegate* unmanaged<int, long>)&Stock,
                SupportedBuild.LocalItemUse => (nint)(delegate* unmanaged<nint, nint, void>)&Use,
                SupportedBuild.RolesClass => (nint)(delegate* unmanaged<nint>)&Class,
                SupportedBuild.FindProxy => (nint)(delegate* unmanaged<nint, nint, nint>)&Proxy,
                SupportedBuild.ReadRoleDilation => (nint)(delegate* unmanaged<GameString*, byte, int>)&Read,
                SupportedBuild.AddRoleDilation => (nint)(delegate* unmanaged<nint, GameString*, byte, int, byte>)&Add,
                SupportedBuild.ReadStoredStamina => (nint)(delegate* unmanaged<GameString*, int*, int*, void>)&ReadStamina,
                SupportedBuild.DeductStamina => (nint)(delegate* unmanaged<GameString*, int, char*, void>)&RestoreStamina,
                SupportedBuild.AddAffinity => (nint)(delegate* unmanaged<nint, nint, int, byte, nint, void>)&Affinity,
                SupportedBuild.UseItemReason => Reason,
                SupportedBuild.CopyString => (nint)(delegate* unmanaged<GameString*, GameString*, nint>)&Copy,
                SupportedBuild.Free => (nint)(delegate* unmanaged<void*, void>)&Free,
                SupportedBuild.Broadcast => (nint)(delegate* unmanaged<nint, void*, void>)&Broadcast,
                SupportedBuild.GameplayLog => (nint)(delegate* unmanaged<char*, void>)&Log,
                _ => throw new InvalidOperationException("Unexpected Repair Gel native API.")
            };
        });
    }

    private void Invoke(int opening, bool badRole = false)
    {
        fixed (char* role = "SyntheticRole")
        {
            ItemRequest value = new()
            {
                Role = new() { Data = role, Length = 14, Capacity = 14 },
                ItemId = ItemUsePatch.RepairGel, Count = 1, RecoverType = opening, Reserved = 0x12345678
            };
            if (badRole) value.Role.Capacity = 1;
            request = (nint)(&value);
            byte[] before = new ReadOnlySpan<byte>(&value, sizeof(ItemRequest)).ToArray();
            ItemUsePatch.UseItem(game, Bag, request);
            Check.True(before.AsSpan().SequenceEqual(new ReadOnlySpan<byte>(&value, sizeof(ItemRequest))), "Borrowed gel request changed.");
        }
        Check.True(valid, "Repair Gel native ABI/ownership/order mismatch.");
        Check.Equal(copies, frees);
    }

    internal static void Verify()
    {
        foreach (bool unlock in new[] { false, true })
        foreach (int opening in new[] { 0, 1 })
        foreach (int before in new[] { 0, 9999, 24567, int.MaxValue })
        {
            var f = new RepairGelFixture { unlock = unlock };
            f.dilation[opening] = before;
            int other = f.dilation[1 - opening];
            f.Invoke(opening);
            Check.Equal(0, f.dilation[opening]); Check.Equal(other, f.dilation[1 - opening]);
            Check.Equal(1, f.consumed); Check.Equal(1, f.copies); Check.Equal(0, f.logs.Count);
            Check.True(f.events.SequenceEqual(new[] { "consume", "read", "reset", "read", "affinity:3", "ui" }), "Reset must finish before affinity/UI and must not invoke the aphrodisiac event.");
            Check.Equal(unlock ? 1 : 0, f.stock); // Native FullUnlock consumption policy remains in charge.
        }
        foreach (string failure in new[] { "read", "native reset", "rejected", "readback", "negative" })
        {
            var f = new RepairGelFixture { failureStage = failure, rejectWrite = failure == "rejected", ignoreWrite = failure == "readback" };
            if (failure == "negative") f.dilation[0] = int.MinValue;
            f.Invoke(0);
            Check.Equal(1, f.consumed); Check.Equal(0, f.copies);
            Check.True(!f.events.Contains("affinity:3") && !f.events.Contains("ui"), "Failure fabricated success.");
            Check.True(f.logs.Count > 0 && f.logs.All(x => x.StartsWith("[Dollhouse items]")), "Missing feature failure diagnostics.");
            if (failure is "read" or "native reset") Check.True(f.logs[0].Contains("stage=" + failure), "Failure stage was lost.");
            if (failure == "readback") Check.True(f.logs[0].Contains("after=24567"), "Missing failed readback value.");
        }
        foreach (bool unlock in new[] { false, true })
        {
            var f = new RepairGelFixture { unlock = unlock }; f.Invoke(2);
            Check.Equal(20, f.stamina); Check.Equal(1, f.consumed); Check.Equal(1, f.copies);
            Check.True(f.events.SequenceEqual(new[] { "consume", "restore", "affinity:3", "ui" }), "Stamina gel completion ordering changed.");
            Check.Equal(0, f.logs.Count); Check.Equal(24567, f.dilation[0]); Check.Equal(37890, f.dilation[1]);
        }
        foreach (int opening in new[] { -1, 3, 99 })
        {
            var f = new RepairGelFixture(); f.Invoke(opening);
            Check.Equal(0, f.consumed); Check.Equal(0, f.events.Count); Check.Equal(1, f.logs.Count);
        }
        var noProxy = new RepairGelFixture { missingProxy = true }; noProxy.Invoke(0);
        Check.Equal(0, noProxy.consumed); Check.True(noProxy.logs.Single().Contains("proxy unavailable"), "Missing proxy diagnosis.");
        var empty = new RepairGelFixture { stock = 0 }; empty.Invoke(1);
        Check.Equal(0, empty.consumed); Check.Equal(0, empty.events.Count);
        var badRole = new RepairGelFixture(); badRole.Invoke(0, badRole: true);
        Check.Equal(0, badRole.consumed); Check.True(badRole.logs.Single().Contains("invalid role string"), "Invalid native string header reached consumption.");
        Current = null!;
    }

    private static bool ValidRole(GameString* role) => role != null && role->Length == 14 && role->Capacity == 14 && new string(role->Data) == "SyntheticRole";
    [UnmanagedCallersOnly] private static byte Mode() => Current.unlock ? (byte)0 : (byte)1;
    [UnmanagedCallersOnly] private static long Stock(int id) { Current.valid &= id == ItemUsePatch.RepairGel; return Current.stock; }
    [UnmanagedCallersOnly] private static nint Class() => RoleClass;
    [UnmanagedCallersOnly] private static nint Proxy(nint bag, nint type)
    { Current.valid &= bag == Bag && type == RoleClass; return Current.missingProxy ? 0 : Roles; }
    [UnmanagedCallersOnly] private static void Use(nint bag, nint request)
    {
        var f = Current;
        f.valid &= bag == Bag && request == f.request;
        if (ItemUsePatch.AvailableStock(f.game, ((ItemRequest*)request)->ItemId) < 1) return;
        f.consumed++; if (!f.unlock) f.stock--; f.events.Add("consume");
        ItemUsePatch.CompleteSuccessfulUse(f.game, bag, request);
    }
    [UnmanagedCallersOnly] private static int Read(GameString* role, byte anal)
    { Current.valid &= ValidRole(role) && anal <= 1; Current.events.Add("read"); return Current.dilation[anal]; }
    [UnmanagedCallersOnly] private static byte Add(nint roles, GameString* role, byte anal, int delta)
    {
        var f = Current;
        f.valid &= roles == Roles && ValidRole(role) && anal <= 1 && delta == -f.dilation[anal];
        f.events.Add("reset");
        if (!f.rejectWrite && !f.ignoreWrite) f.dilation[anal] = Math.Max(0, f.dilation[anal] + delta);
        return f.rejectWrite ? (byte)0 : (byte)1;
    }
    [UnmanagedCallersOnly] private static void ReadStamina(GameString* role, int* current, int* maximum)
    { Current.valid &= ValidRole(role); *current = Current.stamina; *maximum = 20; }
    [UnmanagedCallersOnly] private static void RestoreStamina(GameString* role, int cost, char* reason)
    {
        Current.valid &= ValidRole(role) && cost == -8 && Current.game.StaminaProgressionMode() == 1;
        Current.stamina -= cost; Current.events.Add("restore");
    }
    [UnmanagedCallersOnly] private static void Affinity(nint bag, nint request, int amount, byte notify, nint reason)
    { Current.valid &= bag == Bag && request == Current.request && amount == 3 && notify == 1 && reason == Reason; Current.events.Add("affinity:" + amount); }
    [UnmanagedCallersOnly] private static nint Copy(GameString* target, GameString* source)
    {
        *target = *source; target->Data = (char*)Marshal.AllocHGlobal(source->Length * 2);
        new ReadOnlySpan<char>(source->Data, source->Length).CopyTo(new Span<char>(target->Data, target->Length));
        Current.copies++; return (nint)target;
    }
    [UnmanagedCallersOnly] private static void Free(void* memory) { Marshal.FreeHGlobal((nint)memory); Current.frees++; }
    [UnmanagedCallersOnly] private static void Broadcast(nint address, void* value)
    {
        var f = Current; var result = (ItemRequest*)value;
        f.valid &= address == Bag + SupportedBuild.BagItemUseUIDelegate && ValidRole(&result->Role) &&
            result->ItemId == ItemUsePatch.RepairGel && result->Count == 1 && result->Reserved == 0x12345678 &&
            (result->RecoverType == 2 ? f.stamina == 20 : result->RecoverType is 0 or 1 && f.dilation[result->RecoverType] == 0);
        f.events.Add("ui");
    }
    [UnmanagedCallersOnly] private static void Log(char* message) => Current.logs.Add(new string(message));
}
