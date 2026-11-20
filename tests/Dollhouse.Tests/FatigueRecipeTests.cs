using Dollhouse.Core.Patching;
using Dollhouse.Core.Patching.Native;
using Iced.Intel;
using Build = Dollhouse.Gameplay.SupportedBuild;

namespace Dollhouse.Tests;

internal static class FatigueRecipeTests
{
    internal static void Verify()
    {
        if (GameBinaryFixture.Original is not { } path) return;
        byte[] original = File.ReadAllBytes(path);
        using var image = new PeImage(original);
        var layout = GameplayRuntimePatch.Plan.BuildMemory(original, 0x60000000, _ => 0x2000);
        Instruction Native(int rva) => Decoder.Create(64,
            new ByteArrayCodeReader(original.AsSpan(image.FileOffset(rva), 15).ToArray()), (ulong)rva).Decode();
        foreach (var (start, real) in new[] { (Build.StaminaReaderMode, Build.StaminaReaderReal), (Build.StaminaSummaryMode, Build.StaminaSummaryReal) })
        {
            Check.Equal((ulong)Build.ProfileMode, Native(start).NearBranch64);
            Check.Equal(Mnemonic.Cmp, Native(start + 5).Mnemonic);
            Check.Equal(Mnemonic.Je, Native(start + 7).Mnemonic);
            Check.Equal((ulong)real, Native(start + 7).NearBranch64);
            var edit = layout.Edits.Single(e => e.Rva == start);
            var jump = Decoder.Create(64, new ByteArrayCodeReader(edit.Replacement), (ulong)start).Decode();
            Check.Equal(Mnemonic.Jmp, jump.Mnemonic); Check.Equal((ulong)real, jump.NearBranch64);
        }
        foreach (var (site, target) in new[] {
            (0x53efc7c, 0x549bd60), (0x53f4434, 0x549bd60), (0x54a68f1, Build.ReadStoredStamina),
            (0x54a691f, 0x54a6aa0), (0x54a693c, 0x54481c0), (0x54a6944, 0x549cee0),
            (0x549cf03, 0x551cc10), (0x549cf5f, 0x551ccc0), (0x5428668, Build.ReadIntegerConfig),
            (0x5428679, Build.DeductStamina), (0x543e8b7, Build.DeductStamina),
            (0x53f15c0, Build.FemaleById), (0x53f333b, Build.FemaleById) })
            Check.Equal((ulong)target, Native(site).NearBranch64);
        Check.Equal(360U, Native(0x54a6b62).Immediate32);
        Check.Equal(0x2d82d82d82d82d83UL, Native(0x549be8f).Immediate64);
        Check.Equal(0x1cUL, Native(0x549be29).MemoryDisplacement64);
        Check.Equal(20U, Native(0x549be30).Immediate32);
        Check.Equal(8UL, Native(0x53f3346).MemoryDisplacement64);
        var lockEdit = layout.Edits.Single(e => e.Rva == Build.CardStaminaLock);
        Check.Equal(7, lockEdit.Replacement.Length);
        var exhaustionLock = Decoder.Create(64, new ByteArrayCodeReader(lockEdit.Replacement), (ulong)lockEdit.Rva).Decode();
        Check.Equal(Mnemonic.Or, exhaustionLock.Mnemonic); Check.Equal(Register.RSI, exhaustionLock.MemoryBase);
        Check.Equal(0x4f0UL, exhaustionLock.MemoryDisplacement64); Check.Equal(Register.BPL, exhaustionLock.Op1Register);
        Check.Equal(Register.R15L, Native(Build.CardStaminaLock).Op1Register);
        Check.Equal(0x330UL, Native(0x543c2b9).MemoryDisplacement64); // Native exhausted overlay.
        Check.Equal(Mnemonic.Setne, Native(0x543c2eb).Mnemonic); // Consumes the OR's ZF.
        Check.Equal(0x543c1a0UL, Native(0x53d999b).NearBranch64);
        Check.Equal(0x543c1a0UL, Native(0x53dd563).NearBranch64); // Generic/exec bypass the relay but share this fix.
        Check.True(layout.Edits.Where(e => e.Rva >= 0x549cee0 && e.Rva < 0x549d100)
            .Select(e => e.Rva).SequenceEqual(new[] { Build.StaminaSaveMode }), "Writer widened beyond scoped mode check.");
        Check.True(!layout.Edits.Any(e => e.Rva <= Build.ProfileMode && e.Rva + e.Replacement.Length > Build.ProfileMode), "Global profile mode changed.");
        foreach (var (start, end, roleRegister) in new[] { (Build.DrinkEffectCost, 0x5428683, Register.RSI), (Build.DrinkRequestCost, 0x543e8c1, Register.RBP) })
        {
            var edit = layout.Edits.Single(e => e.Rva == start); Check.Equal(end - start, edit.Replacement.Length);
            var decoder = Decoder.Create(64, new ByteArrayCodeReader(edit.Replacement), (ulong)start);
            var argument = decoder.Decode(); Check.Equal(Register.RCX, argument.Op0Register);
            if (roleRegister == Register.RSI) Check.Equal(roleRegister, argument.Op1Register);
            else { Check.Equal(Mnemonic.Lea, argument.Mnemonic); Check.Equal(roleRegister, argument.MemoryBase); Check.Equal(-0x71L, (long)argument.MemoryDisplacement64); }
            Check.Equal(Mnemonic.Call, decoder.Decode().Mnemonic);
        }
        RuntimeRecipeTests.Verify(original, layout, 0x60000000);
    }
}
