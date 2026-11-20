using System.Runtime.InteropServices;
using Dollhouse.Gameplay;

namespace Dollhouse.Tests;

// Own synchronous native doubles: no game instructions or real saves execute.
internal sealed unsafe class FatigueFixture
{
    private static FatigueFixture Current = null!;
    private readonly NativeGame game;
    private readonly List<string> logs = [];
    private int stamina = 12, maximum = 20, disk = 12, calls, originalCalls, favor, bookkeeping, cost = 3;
    private bool original, failSpendBinding, ignoreSpend, transactionBlocksSave;

    private FatigueFixture()
    {
        Current = this;
        game = new(0, rva => rva switch
        {
            SupportedBuild.ProfileMode => (nint)(delegate* unmanaged<byte>)&Mode,
            SupportedBuild.LocalProgressionGate => (nint)(delegate* unmanaged<byte>)&Mode,
            SupportedBuild.LocalPlayerId => (nint)(delegate* unmanaged<int>)&Player,
            SupportedBuild.LocalRolePropertyChange => (nint)(delegate* unmanaged<int, RolePropertyChanges*, void>)&Properties,
            SupportedBuild.ReadStoredStamina => (nint)(delegate* unmanaged<GameString*, int*, int*, void>)&Read,
            SupportedBuild.DeductStamina when !failSpendBinding => (nint)(delegate* unmanaged<GameString*, int, char*, void>)&Spend,
            SupportedBuild.ReadIntegerConfig => (nint)(delegate* unmanaged<char*, int, int>)&Config,
            SupportedBuild.DrinkStaminaCostKey => 1, // Opaque key passed through to the config stub.
            SupportedBuild.DrinkBookkeeping => (nint)(delegate* unmanaged<void>)&Bookkeeping,
            SupportedBuild.GameplayLog => (nint)(delegate* unmanaged<char*, void>)&Log,
            _ => throw new InvalidOperationException("Synthetic fatigue binding unavailable.")
        });
    }

    private void Enabled(bool enabled)
    {
        fixed (char* key = SettingsBindings.FatigueKey)
        fixed (char* text = enabled ? "1" : "0")
        {
            GameString value = new() { Data = text, Length = 2, Capacity = 2 };
            game.SetSetting(key, &value);
        }
    }

    internal static void Verify()
    {
        using var directory = new TemporaryWorkspace();
        string? previous = Environment.GetEnvironmentVariable(SettingsBindings.PathVariable);
        Environment.SetEnvironmentVariable(SettingsBindings.PathVariable, Path.Combine(directory.Paths.Root, "fatigue.json"));
        try
        {
            foreach (bool original in new[] { false, true })
            foreach (bool enabled in new[] { false, true })
            {
                var f = new FatigueFixture { original = original }; f.Enabled(enabled);
                fixed (char* name = "Role")
                {
                    RolePropertyChange record = new() { Role = new() { Data = name, Length = 5, Capacity = 5 }, AddedFavor = 7, SpentStamina = 1 };
                    RolePropertyChanges changes = new() { Data = &record, Length = 1, Capacity = 1 };
                    f.game.HandleStaminaPropertyChanges(7, &changes);
                    Check.Equal(enabled ? 11 : 12, f.stamina); Check.Equal(f.stamina, f.disk);
                    Check.Equal(original ? 1 : 0, f.originalCalls); Check.Equal(original ? 7 : 0, f.favor);
                    Check.Equal(1, record.SpentStamina); Check.Equal(7, record.AddedFavor);
                    f.game.HandleStaminaPropertyChanges(9, &changes);
                    Check.Equal(enabled ? 11 : 12, f.stamina); // Foreign players are never charged locally.
                    f.game.ApplyDrinkStamina(&record.Role);
                    Check.Equal(enabled ? 8 : 12, f.stamina);
                    Check.Equal(original ? 1 : 0, f.bookkeeping);
                    f.Enabled(!enabled); f.game.ApplyDrinkStamina(&record.Role);
                    Check.Equal(enabled ? 8 : 9, f.stamina); // Live setting, no refill on OFF.
                    ItemRequest item = new() { Role = record.Role, ItemId = ItemUsePatch.RepairGel, RecoverType = 2, Count = 1 };
                    Check.True(f.game.RecoverStamina((nint)(&item)), "Gel recovery failed.");
                    Check.Equal(20, f.stamina); Check.Equal(20, f.disk);
                    Check.Equal(original ? (byte)1 : (byte)0, f.game.StaminaProgressionMode());
                    Check.True(!f.logs.Any(x => x.StartsWith("[Dollhouse fatigue]") || x.StartsWith("[Dollhouse items]")),
                        "Successful stamina operations must not emit feature diagnostics.");
                }
            }
            foreach (int maximum in new[] { 1, 20, 37, int.MaxValue })
            {
                var f = new FatigueFixture { stamina = 0, maximum = maximum }; f.Enabled(false);
                fixed (char* name = "Role")
                {
                    ItemRequest item = new() { Role = new() { Data = name, Length = 5, Capacity = 5 } };
                    Check.True(f.game.RecoverStamina((nint)(&item)), "Gel must refill the native maximum with fatigue OFF.");
                    Check.Equal(maximum, f.stamina);
                    int calls = f.calls; Check.True(f.game.RecoverStamina((nint)(&item)), "Full stamina must remain a valid completion.");
                    Check.Equal(calls, f.calls);
                }
            }
            foreach (string fault in new[] { "binding", "ignored", "invalid", "transaction" })
            {
                var f = new FatigueFixture { failSpendBinding = fault == "binding", ignoreSpend = fault == "ignored",
                    maximum = fault == "invalid" ? 0 : 20, transactionBlocksSave = fault == "transaction" };
                f.Enabled(true);
                f.logs.Clear();
                fixed (char* name = "Role")
                {
                    ItemRequest item = new() { Role = new() { Data = name, Length = 5, Capacity = 5 } };
                    Check.Equal(fault == "transaction", f.game.RecoverStamina((nint)(&item)));
                    Check.Equal((byte)0, f.game.StaminaProgressionMode()); // Exception/failure cannot leak save authorization.
                    if (fault == "transaction") Check.Equal(12, f.disk); // Scope never overrides native transaction guards.
                    else Check.True(f.logs.Count > 0, "Missing failure diagnostics.");
                }
            }
            var configured = new FatigueFixture { cost = 5, stamina = 4 }; configured.Enabled(true);
            fixed (char* name = "Role")
            {
                GameString role = new() { Data = name, Length = 5, Capacity = 5 };
                configured.game.ApplyDrinkStamina(&role); Check.Equal(0, configured.stamina);
                configured.cost = 0; configured.game.ApplyDrinkStamina(&role); Check.Equal(1, configured.calls);
            }
            Check.True(FatiguePolicy.CannotAfford(true, 2, 3) && !FatiguePolicy.CannotAfford(true, 3, 3) &&
                !FatiguePolicy.CannotAfford(false, 0, 3) && !FatiguePolicy.CannotAfford(true, 0, 0), "Drink affordability disagrees with cost policy.");
        }
        finally { Current = null!; Environment.SetEnvironmentVariable(SettingsBindings.PathVariable, previous); }
    }

    [UnmanagedCallersOnly] private static byte Mode() => Current.original ? (byte)1 : (byte)0;
    [UnmanagedCallersOnly] private static int Player() => 7;
    [UnmanagedCallersOnly] private static void Read(GameString* role, int* current, int* maximum)
    { *current = Current.stamina; *maximum = Current.maximum; }
    [UnmanagedCallersOnly] private static void Spend(GameString* role, int cost, char* reason)
    {
        var f = Current; f.calls++;
        if (!f.ignoreSpend) f.stamina = (int)Math.Clamp((long)f.stamina - cost, 0, f.maximum);
        if (f.game.StaminaProgressionMode() == 1 && !f.transactionBlocksSave) f.disk = f.stamina;
    }
    [UnmanagedCallersOnly] private static void Properties(int player, RolePropertyChanges* changes)
    {
        var f = Current; f.originalCalls++;
        for (int i = 0; i < changes->Length; i++)
        {
            f.favor += changes->Data[i].AddedFavor;
            f.stamina = (int)Math.Clamp((long)f.stamina - changes->Data[i].SpentStamina, 0, f.maximum);
            f.disk = f.stamina;
        }
    }
    [UnmanagedCallersOnly] private static int Config(char* key, int fallback) => Current.cost;
    [UnmanagedCallersOnly] private static void Bookkeeping() => Current.bookkeeping++;
    [UnmanagedCallersOnly] private static void Log(char* message) => Current.logs.Add(new string(message));
}
