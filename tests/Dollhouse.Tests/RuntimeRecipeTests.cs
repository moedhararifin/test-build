using System.Buffers.Binary;
using Dollhouse.Core.Patching;
using Dollhouse.Core.Patching.Native;
using Iced.Intel;

namespace Dollhouse.Tests;

/// <summary>Instruction and ownership contracts of the one current in-memory repair set.</summary>
internal static class RuntimeRecipeTests
{
    internal static void Verify()
    {
        if (GameBinaryFixture.Original is not { } fixture) return;
        var original = File.ReadAllBytes(fixture);
        var layout = GameplayRuntimePatch.Plan.BuildMemory(original, 0x60000000, _ => 0x2000);
        Verify(original, layout, 0x60000000);
    }

    internal static void Verify(byte[] original, MemoryPatchLayout layout, int arena)
    {
        using var image = new PeImage(original);
        foreach (var edit in layout.Edits)
            Check.True(original.AsSpan(image.FileOffset(edit.Rva), edit.Expected.Length).SequenceEqual(edit.Expected), "Runtime edit lost its native preimage.");
        Check.True(layout.CodeRva - arena + layout.Code.Length <= 65536, "Runtime layout exceeds its allocation.");
        Instruction CodeAt(int rva) => Decoder.Create(64, new ByteArrayCodeReader(layout.Code.AsSpan(rva - layout.CodeRva).ToArray()), (ulong)rva).Decode();
        ulong Slot(string name) => (ulong)(arena + GameplayRuntimePatch.Plan.ExportNames.ToList().IndexOf(name) * 8);
        void ImportedJump(int rva, string name)
        {
            var jump = CodeAt(rva);
            Check.Equal(Code.Jmp_rm64, jump.Code);
            Check.True(GameplayRuntimePatch.Plan.ExportNames.Contains(name), "Expected runtime export missing.");
            Check.Equal(Slot(name), jump.IPRelativeMemoryAddress);
            Check.True(BinaryPrimitives.ReadInt64LittleEndian(layout.Data.AsSpan((int)jump.IPRelativeMemoryAddress - arena, 8)) != 0,
                "Runtime export slot is unresolved.");
        }
        foreach (var (site, export) in new[]
        {
            (SupportedBuild.CaptureDrainWrite, "DollhouseCaptureDrain"),
            (SupportedBuild.DrainClearedBroadcast, "DollhouseDispatchDrainCleared"),
            (SupportedBuild.DrainRightDirectCall, "DollhouseShowDrainRightMsg"),
            (SupportedBuild.DrainRightExecCall, "DollhouseShowDrainRightMsg"),
            (SupportedBuild.FrameTickerCall, "DollhouseTickCumflation"),
            (SupportedBuild.ClearVolumesBroadcast, "DollhouseNotifyCumflationCleared"),
            (SupportedBuild.DiscardScriptMetadataCall, "DollhouseInstallCameraLoader"),
            (SupportedBuild.ScriptCallDispatch, "DollhouseDispatchBulgeEligibility"),
            (SupportedBuild.ScriptInterfaceDispatch, "DollhouseDispatchBulgeEligibility"),
            (CardFilteringBindings.MatchCardCall, "DollhouseMatchesCardFilters")
        })
        {
            var edit = layout.Edits.Single(edit => edit.Rva == site);
            var call = Decoder.Create(64, new ByteArrayCodeReader(edit.Replacement), (ulong)site).Decode();
            Check.Equal(Mnemonic.Call, call.Mnemonic);
            ImportedJump((int)call.NearBranch64, export);
        }
        var capacity = layout.Edits.Single(edit => edit.Rva == SupportedBuild.ReadRoleAttribute);
        var entry = Decoder.Create(64, new ByteArrayCodeReader(capacity.Replacement), (ulong)capacity.Rva).Decode();
        Check.Equal(Mnemonic.Jmp, entry.Mnemonic);
        int leaf = (int)entry.NearBranch64;
        var decoder = Decoder.Create(64, new ByteArrayCodeReader(layout.Code.AsSpan(leaf - layout.CodeRva).ToArray()), (ulong)leaf);
        var store = decoder.Decode();
        Check.Equal(Mnemonic.Mov, store.Mnemonic); Check.Equal(Register.RBX, store.Op1Register);
        Check.Equal(Register.RSP, store.MemoryBase); Check.Equal(8UL, store.MemoryDisplacement64);
        var key = decoder.Decode();
        Check.Equal(Mnemonic.Cmp, key.Mnemonic); Check.Equal(Register.EDX, key.Op0Register);
        Check.Equal(72, key.Immediate8to32);
        var branch = decoder.Decode();
        Check.Equal(Mnemonic.Jne, branch.Mnemonic); Check.Equal((ulong)SupportedBuild.ReadRoleAttributeResume, branch.NearBranch64);
        ImportedJump((int)decoder.IP, "DollhouseSemenCapacity");

        var metadata = layout.Edits.Single(edit => edit.Rva == SupportedBuild.DiscardScriptMetadataCall);
        Check.Equal(5, metadata.Replacement.Length);
        // The camera installer owns the former metadata-discard call. It retains
        // metadata for all existing script repairs while replacing only the loader.
        Check.Equal((byte)0xe8, metadata.Replacement[0]);
        // All FD_LoadFile stubs are shared with unrelated native functions. The
        // camera repair must never modify their code or registration pointers.
        foreach (int rva in new[] { 0x62ccb10, 0x62cc930, 0x62cd2e0, 0x9e365b8, 0x9e365c0, 0x9e365c8 })
            Check.True(!layout.Edits.Any(edit => edit.Rva <= rva && rva < edit.Rva + edit.Replacement.Length),
                "Direct camera repair changed a shared file-loader binding.");
        Instruction OriginalAt(int rva, int bytes) => Decoder.Create(64,
            new ByteArrayCodeReader(original.AsSpan(image.FileOffset(rva), bytes).ToArray()), (ulong)rva).Decode();
        foreach (int site in new[] { SupportedBuild.ScriptCallDispatch, SupportedBuild.ScriptInterfaceDispatch })
            Check.Equal((ulong)SupportedBuild.ScriptDispatch, OriginalAt(site, 5).NearBranch64);
        var engineStore = OriginalAt(0x50b94f1, 4);
        Check.Equal(Mnemonic.Mov, engineStore.Mnemonic); Check.Equal(Register.RDI, engineStore.Op1Register);
        Check.Equal((ulong)SupportedBuild.ScriptFunctionEngine, engineStore.MemoryDisplacement64);
        var moduleStore = OriginalAt(0x50b94fc, 4);
        Check.Equal(Register.R8, moduleStore.Op1Register);
        Check.Equal((ulong)SupportedBuild.ScriptFunctionModule, moduleStore.MemoryDisplacement64);
        var contextEngine = OriginalAt(0x512961f, 4);
        Check.Equal(Register.RBP, contextEngine.MemoryBase);
        Check.Equal((ulong)SupportedBuild.ScriptContextEngine, contextEngine.MemoryDisplacement64);
        var scalarReturn = OriginalAt(0x50e454e, 7);
        Check.Equal(Mnemonic.Mov, scalarReturn.Mnemonic); Check.Equal(Register.RAX, scalarReturn.Op1Register);
        Check.Equal((ulong)SupportedBuild.ScriptValueRegister, scalarReturn.MemoryDisplacement64);
        var argumentWords = OriginalAt(0x50e4555, 7);
        Check.Equal(Mnemonic.Movsxd, argumentWords.Mnemonic);
        Check.Equal((ulong)SupportedBuild.ScriptArgumentWords, argumentWords.MemoryDisplacement64);
        var popArguments = OriginalAt(0x50e4560, 7);
        Check.Equal(Mnemonic.Add, popArguments.Mnemonic); Check.Equal(Register.RAX, popArguments.Op1Register);
        Check.Equal((ulong)SupportedBuild.ScriptStackPointer, popArguments.MemoryDisplacement64);
        Check.Equal((ulong)SupportedBuild.ScriptFunctionId, OriginalAt(0x512916c, 6).MemoryDisplacement64);
        // Card availability borrows the SAME metadata pointers already selected by
        // native filtering. Keep the stack frame, result list and no-results event.
        var cardFilter = layout.Edits.Single(edit => edit.Rva == CardFilteringBindings.RefreshTail);
        Check.Equal(16, cardFilter.Expected.Length);
        var filterCode = Decoder.Create(64, new ByteArrayCodeReader(cardFilter.Replacement), (ulong)cardFilter.Rva);
        var receiver = filterCode.Decode();
        Check.Equal(Register.RCX, receiver.Op0Register); Check.Equal(Register.R14, receiver.Op1Register);
        var matches = filterCode.Decode();
        Check.Equal(Mnemonic.Lea, matches.Mnemonic); Check.Equal(Register.RDX, matches.Op0Register);
        Check.Equal(Register.RSP, matches.MemoryBase); Check.Equal(0x48UL, matches.MemoryDisplacement64);
        ImportedJump((int)filterCode.Decode().NearBranch64, "DollhouseRefreshCardFilters");
        Check.Equal(Register.RBX, OriginalAt(0x542024e, 4).Op1Register); // FHM_CardData*, not a card ID.
        Check.Equal((ulong)CardFilteringBindings.OriginalCardMatches,
            OriginalAt(CardFilteringBindings.MatchCardCall, 5).NearBranch64);
        Check.True(!layout.Edits.Any(edit => edit.Rva < 0x541f40a && edit.Rva + edit.Replacement.Length > CardFilteringBindings.OriginalCardMatches),
            "ALL selection adapter changed the original native predicate.");
        foreach (var (rva, array) in new[]
        {
            (0x541ef5a, CardFilteringBindings.SelectedFemales), (0x541efbb, CardFilteringBindings.SelectedMales),
            (0x541f01b, CardFilteringBindings.SelectedOrgans), (0x541f0a6, CardFilteringBindings.SelectedConditions)
        })
            Check.Equal((ulong)(array + 8), OriginalAt(rva, 7).MemoryDisplacement64); // Native TArray.Num.
        foreach (var (rva, array) in new[]
        {
            (0x541ef82, CardFilteringBindings.CardCharacters), (0x541f041, CardFilteringBindings.CardOrgans),
            (0x541f070, CardFilteringBindings.CardOtherOrgans), (0x541f0c8, CardFilteringBindings.CardConditions),
            (0x541f0f0, CardFilteringBindings.CardOtherConditions)
        })
            Check.Equal((ulong)array, OriginalAt(rva, 4).MemoryDisplacement64); // Same participant arrays as the native matcher.
        Check.Equal((ulong)CardFilteringBindings.ShowNoFounded, OriginalAt(0x5420948, 5).NearBranch64);
        foreach (var (record, offset) in new[]
        {
            (0x95c36b0, 0x328), (0x95c3730, CardFilteringBindings.EnableIcon),
            (0x95c3770, CardFilteringBindings.Selection), (0x95c37d0, CardFilteringBindings.FemaleGroup),
            (0x95c3810, CardFilteringBindings.MaleGroup), (0x95c3850, CardFilteringBindings.BadgeText),
            (0x95cc800, CardFilteringBindings.EnableIcon), (0x95cc840, CardFilteringBindings.Selection),
            (0x95cbc00, CardFilteringBindings.FemalePanel), (0x95cbc40, CardFilteringBindings.MalePanel),
            (0x95cbc80, CardFilteringBindings.OrganPanel), (0x95cbcc0, CardFilteringBindings.ConditionPanel),
            (0x95cbd00, CardFilteringBindings.StatePanel), (0x95cbd40, CardFilteringBindings.SmilePanel),
            (0x95cbd80, CardFilteringBindings.RarePanel)
        })
            Check.Equal(offset, BitConverter.ToInt32(original, record + 52)); // Native property records (raw).
        Check.Equal((ulong)CardFilteringBindings.HeadEnabled, OriginalAt(0x5434fd4, 7).MemoryDisplacement64);
        Check.Equal((ulong)CardFilteringBindings.BodyEnabled, OriginalAt(0x5444844, 7).MemoryDisplacement64);
        // Fetish rows use XPData.Theme (+9), independently from Genre (+8).
        Check.Equal(8, OriginalAt(0x54447bf, 4).Immediate8to64);
        Check.Equal(9, OriginalAt(0x54447f3, 4).Immediate8to64);
        Check.Equal((ulong)CardFilteringBindings.OrganValue, OriginalAt(0x5423552, 6).MemoryDisplacement64);
        Check.Equal((ulong)CardFilteringBindings.ConditionValue, OriginalAt(0x542355c, 6).MemoryDisplacement64);
        var areaRelay = layout.Edits.Single(edit => edit.Rva == CardFilteringBindings.CardLockRelay);
        Check.Equal((ulong)CardFilteringBindings.OriginalCardLock,
            Decoder.Create(64, new ByteArrayCodeReader(areaRelay.Expected), (ulong)areaRelay.Rva).Decode().NearBranch64);
        int areaLeaf = (int)Decoder.Create(64, new ByteArrayCodeReader(areaRelay.Replacement), (ulong)areaRelay.Rva).Decode().NearBranch64;
        ImportedJump(areaLeaf, "DollhouseSetCardLock");
        Check.Equal(CardFilteringBindings.CardLocation, BitConverter.ToInt32(original, 0x9570ed0 + 52));
        Check.Equal(CardFilteringBindings.CardAreas, BitConverter.ToInt32(original, 0x9571470 + 52));
        // Native enum entries: Floor=0, Wall=1, Prop=2; Room_ALL=1.
        Check.Equal(0L, BitConverter.ToInt64(original, 0x9571be8));
        Check.Equal(1L, BitConverter.ToInt64(original, 0x9571bf8));
        Check.Equal(2L, BitConverter.ToInt64(original, 0x9571c08));
        Check.Equal(1L, BitConverter.ToInt64(original, 0x95c5e08));
        Check.Equal(0x54bb080UL, OriginalAt(CardFilteringBindings.CurrentMapName + 9, 5).NearBranch64);
        // Resolve the reflected method by its VERIFIED name, not a generated
        // wrapper with a coincidentally compatible array signature (SetMaleInfo).
        var areaNameText = OriginalAt(0x1019670, 7);
        Check.Equal(Mnemonic.Lea, areaNameText.Mnemonic);
        Check.Equal("SetUsableInIcon", System.Text.Encoding.Unicode.GetString(original,
            image.FileOffset((int)areaNameText.IPRelativeMemoryAddress), 32).TrimEnd('\0'));
        Check.Equal((ulong)CardFilteringBindings.UsableAreasName, OriginalAt(0x1019677, 7).IPRelativeMemoryAddress);
        Check.Equal((ulong)CardFilteringBindings.FindWidgetFunction, OriginalAt(0x15bf12c, 5).NearBranch64);
        foreach (var (binding, initializer, name) in new[]
        {
            (CardFilteringBindings.FilterSelected, 0x10191b0, "SetSelected"),
            (CardFilteringBindings.RareSelected, 0x10196d0, "SetSelected"),
            (CardFilteringBindings.ShowNoFounded, 0x1019590, "ShowNoFounded")
        })
        {
            int textRva = (int)OriginalAt(initializer, 7).IPRelativeMemoryAddress;
            Check.Equal(name, System.Text.Encoding.Unicode.GetString(original,
                image.FileOffset(textRva), (name.Length + 1) * 2).TrimEnd('\0'));
            Check.Equal(OriginalAt(initializer + 7, 7).IPRelativeMemoryAddress,
                OriginalAt(binding + 0xd, 7).IPRelativeMemoryAddress);
        }
        // UTextBlock's real virtual SetText consumes the by-value FText reference.
        Check.Equal(0x1436cfd30L, BitConverter.ToInt64(original,
            image.FileOffset(0x8afcb20 + CardFilteringBindings.TextSlot)));
        Check.Equal(0x129f9f0UL, OriginalAt(CardFilteringBindings.TextFromLiteral + 0x34, 5).NearBranch64);
        Check.Equal(Register.RSI, OriginalAt(0x36cfde5, 3).MemoryBase);
        Check.Equal(0x10UL, OriginalAt(0x36cfdf0, 3).MemoryDisplacement64); // Release native text reference.
        // Repair Gel reuses existing hooks; validate the selected-opening native
        // writer/readback ABI and UI delegate against this exact original image.
        Check.Equal((ulong)SupportedBuild.ReadRoleDilation, OriginalAt(0x53f1720, 5).NearBranch64);
        Check.Equal(0x545ea80UL, OriginalAt(SupportedBuild.AddRoleDilation + 0x4a, 5).NearBranch64);
        Check.Equal(Mnemonic.Add, OriginalAt(0x545eb31, 3).Mnemonic);
        Check.Equal(Mnemonic.Cmovs, OriginalAt(0x545eb34, 4).Mnemonic); // Clamp negative sum to zero.
        Check.Equal(10000U, OriginalAt(0x53f1746, 6).Immediate32); // Level/progress packing.
        Check.Equal(0xd8U, OriginalAt(0x53f08cf, 7).Immediate32);
        Check.Equal((ulong)SupportedBuild.Broadcast, OriginalAt(0x53f08f3, 5).NearBranch64);
        Check.Equal(0x5478470UL, OriginalAt(0x5359180, 5).NearBranch64);
        Check.Equal(100001U, OriginalAt(0x5478486, 5).Immediate32);
        Check.Equal(100001 - 17, Dollhouse.Gameplay.ItemUsePatch.RepairGel);
        Check.Equal((uint)SupportedBuild.BagItemUseUIDelegate,
            BinaryPrimitives.ReadUInt32LittleEndian(original.AsSpan(image.FileOffset(0x952da84), 4)));
        // Direct loaded-function lookup and scoped default-namespace APIs. No
        // declaration-parser/compiler entry is used by the camera repair.
        foreach (var (slot, target) in new[] { (0x48, SupportedBuild.ScriptModuleSetNamespace),
            (0x50, SupportedBuild.ScriptModuleGetNamespace), (0x70, SupportedBuild.ScriptModuleFunctionByName) })
            Check.Equal(0x140000000L + target,
                BinaryPrimitives.ReadInt64LittleEndian(original.AsSpan(image.FileOffset(0x94d7168 + slot), 8)));
        foreach (int rva in new[] { 0x513d8cc, 0x513b7d0, 0x5196a76 })
            Check.Equal(0xc0UL, OriginalAt(rva, 7).MemoryDisplacement64);
        Check.Equal(0x513e560UL, OriginalAt(0x513d8ef, 5).NearBranch64);
        foreach (int rva in new[] { 0x50e44b0, 0x51291d9 })
        {
            var vmEntry = OriginalAt(rva, 7);
            Check.Equal(Mnemonic.Mov, vmEntry.Mnemonic);
            Check.Equal((ulong)SupportedBuild.ScriptVmEntry, vmEntry.MemoryDisplacement64);
            Check.Equal(Register.R9, vmEntry.Op0Register);
        }
        var engine = OriginalAt(0x533e726, 7);
        Check.Equal(Mnemonic.Lea, engine.Mnemonic);
        Check.Equal((ulong)SupportedBuild.PrecompiledEngine, engine.MemoryDisplacement64);
        var arguments = OriginalAt(0x50e4555, 7);
        Check.Equal(Mnemonic.Movsxd, arguments.Mnemonic);
        Check.Equal((ulong)SupportedBuild.ScriptArgumentWords, arguments.MemoryDisplacement64);
        var kind = OriginalAt(0x51291ca, 6);
        Check.Equal((ulong)SupportedBuild.ScriptFunctionKind, kind.MemoryDisplacement64);
        foreach (var (rva, offset) in new[] { (0x533f7f7, SupportedBuild.ScriptVmEntry),
            (0x533f802, SupportedBuild.ScriptParameterEntry), (0x533f80d, SupportedBuild.ScriptRawEntry) })
            Check.Equal((ulong)offset, OriginalAt(rva, 7).MemoryDisplacement64);
        foreach (int protectedRva in new[] { SupportedBuild.LocalProgressionGate, SupportedBuild.Constant100Function })
            Check.True(!layout.Edits.Any(edit => edit.Rva <= protectedRva && protectedRva < edit.Rva + edit.Replacement.Length),
                "Scoped repairs changed a global mode/threshold function.");

        var item = layout.Edits.Single(edit => edit.Rva == SupportedBuild.SuccessfulOrdinaryItem);
        decoder = Decoder.Create(64, new ByteArrayCodeReader(item.Replacement), (ulong)item.Rva);
        decoder.Decode(); decoder.Decode();
        var count = decoder.Decode();
        Check.Equal(Register.R8D, count.Op0Register); Check.Equal(Register.EBX, count.Op1Register);

        var persist = layout.Edits.Single(edit => edit.Rva == SupportedBuild.PropertyDirectDispatch);
        decoder = Decoder.Create(64, new ByteArrayCodeReader(persist.Replacement), (ulong)persist.Rva);
        var context = decoder.Decode();
        Check.Equal(Register.R8, context.Op0Register); Check.Equal(Register.RCX, context.Op1Register);
        decoder.Decode(); decoder.Decode();
        var dispatch = decoder.Decode();
        Check.Equal(Mnemonic.Call, dispatch.Mnemonic);
        ImportedJump((int)dispatch.NearBranch64, "DollhousePersistCumflationWithContext");
        Check.Equal((ulong)SupportedBuild.PropertyDirectCleanup, decoder.Decode().NearBranch64);
    }
}
