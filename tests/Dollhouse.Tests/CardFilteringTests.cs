using System.Runtime.InteropServices;
using Dollhouse.Gameplay;
using B = Dollhouse.Gameplay.CardFilteringBindings;

namespace Dollhouse.Tests;

// Only own native objects/vtables are executed. No game code, windows or saves.
internal sealed unsafe class CardFilteringTests : IDisposable
{
    private static CardFilteringTests Current = null!;
    private readonly List<nint> allocations = [];
    private readonly Dictionary<nint, nint[]> panels = [];
    private readonly Dictionary<nint, byte> visibility = [];
    private readonly Dictionary<nint, string> badges = [];
    private readonly Dictionary<nint, byte> styles = [];
    private readonly HashSet<nint> childArrays = [];
    private readonly HashSet<nint> mapStrings = [];
    private readonly HashSet<nint> eventArrays = [];
    private readonly List<string> events = [];
    private readonly nint headClass, bodyClass, filterClass, rareClass, foreignClass, vtable;
    private readonly nint selector;
    private readonly nint usableAreasName;
    private readonly NativeGame game;
    private int textCreated, textConsumed, freedChildren;
    private string mapName = "Room_Club";
    private int mapReads, areaTips, nativeLocks;
    private int nativeMatches;
    private byte nativeMatchResult = 1;
    private bool missingAreaFunction;
    private int[] cardStamina = [];
    private nint borrowedAreas;
    private const ulong AreaFunctionName = 0x123400005678;
    private const nint AreaFunction = 0x4321;

    private nint Allocate(int bytes)
    {
        nint p = (nint)NativeMemory.AllocZeroed((nuint)bytes);
        allocations.Add(p);
        return p;
    }

    private nint Class()
    {
        nint type = Allocate(0x60), ancestors = Allocate(8);
        *(int*)(type + 0x38) = 0;
        *(nint*)(type + 0x30) = ancestors;
        *(nint*)ancestors = type + 0x30;
        return type;
    }

    private CardFilteringTests()
    {
        Current = this;
        headClass = Class(); bodyClass = Class(); filterClass = Class(); rareClass = Class(); foreignClass = Class();
        selector = Allocate(0x650);
        usableAreasName = Allocate(8); *(ulong*)usableAreasName = AreaFunctionName;
        vtable = Allocate(0x380);
        *(nint*)(vtable + B.VisibilitySlot) = (nint)(delegate* unmanaged<nint, byte, void>)&Visibility;
        *(nint*)(vtable + B.TextSlot) = (nint)(delegate* unmanaged<nint, CardFilterText*, void>)&Text;
        *(nint*)(vtable + B.ProcessEventSlot) = (nint)(delegate* unmanaged<nint, nint, CardFilterArray<byte>*, void>)&AreaEvent;
        game = new(0, rva => rva switch
        {
            B.ShowNoFounded => (nint)(delegate* unmanaged<nint, byte, void>)&NoResults,
            B.PanelChildren => (nint)(delegate* unmanaged<nint, CardFilterArray<nint>*, nint>)&Children,
            B.HeadClass => (nint)(delegate* unmanaged<nint>)&HeadType,
            B.BodyClass => (nint)(delegate* unmanaged<nint>)&BodyType,
            B.FilterClass => (nint)(delegate* unmanaged<nint>)&FilterType,
            B.RareClass => (nint)(delegate* unmanaged<nint>)&RareType,
            B.FilterSelected or B.RareSelected => (nint)(delegate* unmanaged<nint, byte, void>)&Style,
            B.TextFromLiteral => (nint)(delegate* unmanaged<CardFilterText*, char*, CardFilterText*>)&FromLiteral,
            B.OriginalCardLock => (nint)(delegate* unmanaged<nint, byte, byte, int>)&OriginalLock,
            B.OriginalCardMatches => (nint)(delegate* unmanaged<nint, nint, nint, byte>)&OriginalMatch,
            B.CurrentMapName => (nint)(delegate* unmanaged<GameString*, GameString*>)&MapName,
            B.UsableAreasName => usableAreasName,
            B.FindWidgetFunction => (nint)(delegate* unmanaged<nint, ulong, int, nint>)&FindAreaFunction,
            Dollhouse.Gameplay.SupportedBuild.Allocate => (nint)(delegate* unmanaged<nuint, uint, void*>)&AllocateEvent,
            Dollhouse.Gameplay.SupportedBuild.GameplayLog => (nint)(delegate* unmanaged<char*, void>)&Log,
            Dollhouse.Gameplay.SupportedBuild.Free => (nint)(delegate* unmanaged<void*, void>)&FreeChildren,
            _ => throw new InvalidOperationException($"Unexpected card-filter API {rva:x}.")
        });
    }

    public void Dispose()
    {
        foreach (nint pointer in allocations) NativeMemory.Free((void*)pointer);
        foreach (nint pointer in childArrays) NativeMemory.Free((void*)pointer);
        foreach (nint pointer in mapStrings) NativeMemory.Free((void*)pointer);
        foreach (nint pointer in eventArrays) NativeMemory.Free((void*)pointer);
        Current = null!;
    }

    private CardFilterArray<T> Array<T>(params T[] values) where T : unmanaged
    {
        T* data = (T*)Allocate(Math.Max(1, values.Length) * sizeof(T));
        values.AsSpan().CopyTo(new Span<T>(data, values.Length));
        return new() { Data = data, Count = values.Length, Capacity = values.Length };
    }

    private void Values(nint owner, int offset, params byte[] values) =>
        *(CardFilterArray<byte>*)(owner + offset) = Array(values);

    private nint Widget(nint type)
    {
        nint widget = Allocate(0x500);
        *(nint*)widget = vtable;
        *(nint*)(widget + 0x10) = type;
        return widget;
    }

    private nint Control(nint widget, int offset)
    {
        nint control = Widget(foreignClass);
        *(nint*)(widget + offset) = control;
        return control;
    }

    private nint Head(byte character, bool female)
    {
        nint widget = Widget(headClass);
        *(byte*)(widget + B.HeadCharacter) = character;
        *(byte*)(widget + B.HeadIsFemale) = (byte)(female ? 1 : 0);
        foreach (int offset in new[] { B.EnableIcon, B.Selection, B.FemaleGroup, B.MaleGroup, B.BadgeText }) Control(widget, offset);
        // Portrait and script-only decorations must remain untouched.
        *(nint*)(widget + 0x330) = (nint)0x12345678;
        *(long*)(widget + 0x3d8) = 0x1122334455667788;
        return widget;
    }

    private nint Body(byte organ, byte condition = 0)
    {
        nint widget = Widget(bodyClass);
        *(byte*)(widget + B.OrganValue) = organ;
        *(byte*)(widget + B.ConditionValue) = condition;
        Control(widget, B.EnableIcon); Control(widget, B.Selection);
        return widget;
    }

    private void Panel(int member, params nint[] children)
    {
        nint panel = Allocate(8);
        panels.Add(panel, children);
        *(nint*)(selector + member) = panel;
    }

    private byte Visible(nint widget, int member) => visibility[*(nint*)(widget + member)];
    private string Badge(nint widget) => badges[*(nint*)(widget + B.BadgeText)];

    internal static void Verify()
    {
        Check.Equal(16, sizeof(CardFilterArray<nint>));
        Check.Equal(16, sizeof(CardFilterText));
        using var f = new CardFilteringTests();
        f.VerifyRefresh();
        Check.True(!f.events.Any(x => x.StartsWith("[Dollhouse card filtering]", StringComparison.Ordinal)),
            "Successful filtering emitted production diagnostics.");
        f.VerifyAreaLocks();
        f.VerifyFatigueLocks();
        f.VerifyMatching();
    }

    private void VerifyRefresh()
    {
        nint first = Head(1, true), second = Head(2, true), unavailable = Head(3, true), male = Head(11, false);
        // Conditions intentionally share Genre=0 and differ only by Theme. This
        // catches the observed all-selected/none-selected fetish presentation bug.
        nint organ = Body(20), pairedOrgan = Body(21), otherOrgan = Body(22), condition = Body(0, 30), pairedCondition = Body(0, 31);
        nint state = Widget(filterClass), smile = Widget(filterClass), rare = Widget(rareClass), foreign = Widget(foreignClass);
        *(byte*)(state + B.FilterState) = 2; *(byte*)(smile + B.FilterSmile) = 4; *(int*)(rare + B.RareValue) = 9;
        *(byte*)(selector + B.SelectedState) = 2; *(byte*)(selector + B.SelectedSmile) = 4; *(int*)(selector + B.SelectedRare) = 9;
        Panel(B.FemalePanel, first, second, unavailable, foreign, 0);
        Panel(B.MalePanel, male);
        Panel(B.OrganPanel, organ, pairedOrgan, otherOrgan);
        Panel(B.ConditionPanel, condition, pairedCondition);
        Panel(B.StatePanel, state); Panel(B.SmilePanel, smile); Panel(B.RarePanel, rare);
        Values(selector, B.SelectedFemales, 2, 1); Values(selector, B.SelectedMales, 11);
        Values(selector, B.SelectedOrgans, 21); Values(selector, B.SelectedConditions, 30);

        nint card = Allocate(0x100);
        Values(card, B.CardCharacters, 1, 2, 11, 1);
        Values(card, B.CardOrgans, 20); Values(card, B.CardOtherOrgans, 21);
        Values(card, B.CardConditions, 30); Values(card, B.CardOtherConditions, 31);
        var matches = Array(card, card); // Duplicates must not affect availability or order.
        byte[] selectorBefore = new ReadOnlySpan<byte>((void*)(selector + B.SelectedFemales), 0x48).ToArray();
        byte[] cardBefore = new ReadOnlySpan<byte>((void*)card, 0x100).ToArray();
        game.RefreshCardFilters(selector, &matches);
        Check.Equal("no-results:0", events[0]);
        Check.Equal((byte)1, *(byte*)(first + B.HeadEnabled)); Check.Equal((byte)0, *(byte*)(unavailable + B.HeadEnabled));
        Check.Equal((byte)2, Visible(first, B.EnableIcon)); Check.Equal((byte)0, Visible(unavailable, B.EnableIcon));
        Check.Equal((byte)0, Visible(first, B.Selection)); Check.Equal((byte)2, Visible(unavailable, B.Selection));
        Check.Equal("B", Badge(first)); Check.Equal("A", Badge(second)); Check.Equal("A", Badge(male));
        Check.Equal((byte)0, Visible(first, B.FemaleGroup)); Check.Equal((byte)2, Visible(first, B.MaleGroup));
        Check.Equal((byte)2, Visible(male, B.FemaleGroup)); Check.Equal((byte)0, Visible(male, B.MaleGroup));
        Check.Equal((byte)1, *(byte*)(pairedOrgan + B.BodyEnabled)); Check.Equal((byte)1, *(byte*)(pairedCondition + B.BodyEnabled));
        Check.Equal((byte)0, *(byte*)(otherOrgan + B.BodyEnabled));
        Check.Equal((byte)0, Visible(pairedOrgan, B.Selection)); Check.Equal((byte)2, Visible(organ, B.Selection));
        Check.Equal((byte)0, Visible(condition, B.Selection)); Check.Equal((byte)2, Visible(pairedCondition, B.Selection));
        Check.Equal((byte)1, styles[state]); Check.Equal((byte)1, styles[smile]); Check.Equal((byte)1, styles[rare]);
        Check.Equal((nint)0x12345678, *(nint*)(first + 0x330)); Check.Equal(0x1122334455667788L, *(long*)(first + 0x3d8));
        Check.True(selectorBefore.AsSpan().SequenceEqual(new ReadOnlySpan<byte>((void*)(selector + B.SelectedFemales), 0x48)), "Selection state changed during presentation refresh.");
        Check.True(cardBefore.AsSpan().SequenceEqual(new ReadOnlySpan<byte>((void*)card, 0x100)), "Matching card metadata changed.");
        Check.Equal(7, freedChildren); Check.Equal(0, childArrays.Count);
        Check.Equal(textCreated, textConsumed);

        // Deselecting/reordering must recalculate A/B; stale widgets become available again.
        Values(selector, B.SelectedFemales, 1);
        Values(card, B.CardCharacters, 1, 3);
        // Zero is also a valid fetish enum: selecting it must not select the
        // other row just because both rows' unrelated Genre field is zero.
        *(byte*)(condition + B.ConditionValue) = 0;
        Values(selector, B.SelectedConditions, 0);
        Values(card, B.CardConditions, 0);
        *(byte*)(selector + B.SelectedState) = 0; *(byte*)(selector + B.SelectedSmile) = 0; *(int*)(selector + B.SelectedRare) = 0;
        game.RefreshCardFilters(selector, &matches);
        Check.Equal("A", Badge(first)); Check.Equal((byte)2, Visible(second, B.Selection));
        Check.Equal((byte)0, Visible(condition, B.Selection)); Check.Equal((byte)2, Visible(pairedCondition, B.Selection));
        Check.Equal((byte)1, *(byte*)(unavailable + B.HeadEnabled));
        Check.Equal((byte)0, styles[state]); Check.Equal((byte)0, styles[smile]); Check.Equal((byte)0, styles[rare]);

        // Empty results disable choices without silently clearing selected lists/badges.
        var empty = Array<nint>();
        game.RefreshCardFilters(selector, &empty);
        Check.True(events.Contains("no-results:1"), "Empty result notification missing.");
        Check.Equal((byte)0, *(byte*)(first + B.HeadEnabled)); Check.Equal("A", Badge(first));
        Check.Equal((byte)0, *(byte*)(organ + B.BodyEnabled));
        Check.Equal(textCreated, textConsumed); Check.Equal(0, childArrays.Count);

        // Invalid selected-array input must still release the panel's child array.
        int freed = freedChildren;
        *(CardFilterArray<byte>*)(selector + B.SelectedFemales) = new() { Count = 1, Capacity = 0 };
        Check.Throws<InvalidDataException>(() => RefreshInvalid());
        Check.Equal(freed + 1, freedChildren); Check.Equal(0, childArrays.Count);
    }

    private void RefreshInvalid() { var empty = Array<nint>(); game.RefreshCardFilters(selector, &empty); }

    private void VerifyMatching()
    {
        nint card = Allocate(0x190);
        const nint context = 0x1234;
        foreach (int offset in new[] { B.SelectedFemales, B.SelectedMales, B.SelectedOrgans, B.SelectedConditions }) Values(selector, offset);
        foreach (int offset in new[] { B.CardCharacters, B.CardOrgans, B.CardOtherOrgans, B.CardConditions, B.CardOtherConditions }) Values(card, offset);

        Values(selector, B.SelectedFemales, 1);
        Values(selector, B.SelectedMales, 10, 11);
        Values(card, B.CardCharacters, 1, 10);
        Check.Equal((byte)0, game.MatchesCardFilters(selector, card, context));
        Values(card, B.CardCharacters, 1, 11);
        Check.Equal((byte)0, game.MatchesCardFilters(selector, card, context));
        Check.Equal(0, nativeMatches); // Neither individual experiment card satisfies A+B.
        Values(card, B.CardCharacters, 1, 10, 11);
        Check.Equal((byte)1, game.MatchesCardFilters(selector, card, context));
        Check.Equal(1, nativeMatches);
        nativeMatchResult = 0; // Native ownership/rarity/search may still reject a full participant match.
        Check.Equal((byte)0, game.MatchesCardFilters(selector, card, context));
        nativeMatchResult = 1;

        Values(selector, B.SelectedFemales, 1, 2);
        Check.Equal((byte)0, game.MatchesCardFilters(selector, card, context));
        Values(card, B.CardCharacters, 1, 2, 10, 11);
        Check.Equal((byte)1, game.MatchesCardFilters(selector, card, context));
        Values(selector, B.SelectedOrgans, 20, 21);
        Values(card, B.CardOrgans, 20); Values(card, B.CardOtherOrgans, 21);
        Check.Equal((byte)1, game.MatchesCardFilters(selector, card, context));
        Values(card, B.CardOtherOrgans);
        Check.Equal((byte)0, game.MatchesCardFilters(selector, card, context));
        Values(card, B.CardOtherOrgans, 21);
        Values(selector, B.SelectedConditions, 0, 31);
        Values(card, B.CardConditions, 0); Values(card, B.CardOtherConditions, 31);
        Check.Equal((byte)1, game.MatchesCardFilters(selector, card, context));
        Values(card, B.CardOtherConditions);
        Check.Equal((byte)0, game.MatchesCardFilters(selector, card, context));

        // Selection expansion cannot reveal new cards or left-side choices.
        byte[][] rows = [[1, 2], [2, 3], [3, 4]];
        var one = rows.Where(row => CardSelectionPolicy.ContainsEvery([1], row)).ToArray();
        var two = rows.Where(row => CardSelectionPolicy.ContainsEvery([1, 2], row)).ToArray();
        Check.Equal(1, one.Length); Check.Equal(1, two.Length);
        Check.True(!two.SelectMany(row => row).Contains((byte)3), "Adding B broadened A into B+C cards.");
        for (int subset = 0; subset < 16; subset++)
        for (int superset = 0; superset < 16; superset++)
        {
            if ((subset & superset) != subset) continue;
            byte[] before = Enumerable.Range(1, 4).Where(i => (subset & (1 << (i - 1))) != 0).Select(i => (byte)i).ToArray();
            byte[] after = Enumerable.Range(1, 4).Where(i => (superset & (1 << (i - 1))) != 0).Select(i => (byte)i).ToArray();
            foreach (byte[] row in rows)
                Check.True(!CardSelectionPolicy.ContainsEvery(after, row) || CardSelectionPolicy.ContainsEvery(before, row),
                    "Adding a selection introduced a new matching card.");
        }
        foreach (int offset in new[] { B.SelectedFemales, B.SelectedMales, B.SelectedOrgans, B.SelectedConditions }) Values(selector, offset);
        Check.Equal((byte)1, game.MatchesCardFilters(selector, card, context)); // Clearing removes the extra constraint.
    }

    private void VerifyFatigueLocks()
    {
        nint pose = Widget(foreignClass);
        foreach (int member in new[] { B.PoseTiredGroup, B.PoseLockGroup, B.PoseLockReason, B.PoseUsableAreas }) Control(pose, member);
        // A prop in another area must not overwrite the native tired reason.
        nint card = pose + B.PoseData;
        *(byte*)(card + B.CardLocation) = CardAreaPolicy.Prop; Values(card, B.CardAreas, 9);
        int before = mapReads;
        foreach (int[] stamina in new int[][] { [0], [20, 0], [0, 20], [0, 0] })
        {
            cardStamina = stamina;
            Check.Equal(1, game.SetCardLock(pose, 0, 0));
            Check.Equal((byte)1, *(byte*)(pose + B.PoseLocked));
            Check.Equal((byte)0, *(byte*)(pose + 0x4f1)); // No experience bypass introduced.
            Check.Equal((byte)0, *(byte*)(pose + 0x4f2)); // Still owned.
            Check.Equal((byte)4, Visible(pose, B.PoseTiredGroup));
            Check.Equal(before, mapReads); // Exhaustion wins before area presentation.
        }
        *(byte*)(card + B.CardLocation) = CardAreaPolicy.Floor;
        cardStamina = [1, 20];
        Check.Equal(0, game.SetCardLock(pose, 0, 0)); // Gel/recovery clears the next native reevaluation.
        Check.Equal((byte)0, *(byte*)(pose + B.PoseLocked));
        Check.Equal((byte)1, Visible(pose, B.PoseTiredGroup));
        Check.Equal(1, game.SetCardLock(pose, 1, 0)); // Ownership still locks a recovered card.
        cardStamina = [];
    }

    private void VerifyAreaLocks()
    {
        nint pose = Widget(foreignClass);
        foreach (int member in new[] { B.PoseTiredGroup, B.PoseLockGroup, B.PoseLockReason, B.PoseUsableAreas }) Control(pose, member);
        nint card = pose + B.PoseData;
        Values(card, B.CardAreas, 9); // Pool-only prop, while the current map is Club.
        borrowedAreas = (nint)((CardFilterArray<byte>*)(card + B.CardAreas))->Data;
        *(byte*)(card + B.CardLocation) = CardAreaPolicy.Prop;
        byte[] areaHeader = new ReadOnlySpan<byte>((void*)(card + B.CardAreas), 16).ToArray();
        Check.Equal(1, game.SetCardLock(pose, 0, 0));
        Check.Equal((byte)1, *(byte*)(pose + B.PoseLocked));
        Check.Equal((byte)0, Visible(pose, B.PoseLockGroup));
        Check.Equal((byte)2, Visible(pose, B.PoseTiredGroup));
        Check.Equal("Required prop unavailable in this area.", badges[*(nint*)(pose + B.PoseLockReason)]);
        Check.Equal(1, areaTips); Check.Equal(1, mapReads);
        Check.Equal(0, eventArrays.Count);
        Check.Equal((byte)0, *(byte*)(pose + 0x4f2)); // Not falsely marked unowned.
        Check.True(areaHeader.AsSpan().SequenceEqual(new ReadOnlySpan<byte>((void*)(card + B.CardAreas), 16)), "Borrowed area list changed.");

        // Native reevaluation removes the area overlay after travelling to a supported map.
        mapName = "Room_Pool";
        Check.Equal(0, game.SetCardLock(pose, 0, 0));
        Check.Equal((byte)0, *(byte*)(pose + B.PoseLocked));
        Check.Equal((byte)2, Visible(pose, B.PoseLockGroup));
        mapName = "Room_Club";
        foreach (byte location in new[] { CardAreaPolicy.Floor, CardAreaPolicy.Wall })
        {
            *(byte*)(card + B.CardLocation) = location;
            int reads = mapReads;
            Check.Equal(0, game.SetCardLock(pose, 0, 0));
            Check.Equal(reads, mapReads); // Cross-area floor/wall never needs an area check.
        }
        *(byte*)(card + B.CardLocation) = CardAreaPolicy.Prop;
        Values(card, B.CardAreas, CardAreaPolicy.AllAreas);
        Check.Equal(0, game.SetCardLock(pose, 0, 0));
        Values(card, B.CardAreas);
        Check.Equal(0, game.SetCardLock(pose, 0, 0));
        Values(card, B.CardAreas, 9);
        int before = mapReads;
        Check.Equal(1, game.SetCardLock(pose, 1, 1)); // Preserve native ownership lock and experience flag.
        Check.Equal(before, mapReads); Check.Equal((byte)1, *(byte*)(pose + 0x4f1));

        // Unknown/loading maps leave native behavior intact and retain a failure diagnostic.
        mapName = "LoadingScreen";
        Check.Equal(0, game.SetCardLock(pose, 0, 0));
        Check.True(events.Any(x => x.Contains("Current card area")), "Unknown map failure was silent.");
        Check.Equal(0, game.SetCardLock(pose, 0, 0));
        Check.Equal(1, events.Count(x => x.Contains("Current card area"))); // Same failure across many cards is one diagnostic.
        // A missing reflected function must not call FindFunctionChecked/ProcessEvent,
        // must keep the prop card locked, and must not allocate/leak event parameters.
        mapName = "Room_Club"; missingAreaFunction = true;
        int submitted = areaTips;
        Check.Equal(1, game.SetCardLock(pose, 0, 0));
        Check.Equal(submitted, areaTips); Check.Equal(0, eventArrays.Count);
        Check.Equal((byte)1, *(byte*)(pose + B.PoseLocked));
        Check.True(events.Any(x => x.Contains("no SetUsableInIcon")), "Missing-method failure was silent.");
        Check.Equal(1, game.SetCardLock(pose, 0, 0));
        Check.Equal(1, events.Count(x => x.Contains("no SetUsableInIcon")));
        Check.True(events.Where(x => x.StartsWith("[Dollhouse card filtering]", StringComparison.Ordinal))
            .All(x => x.Contains("area lock failed:")), "Failure diagnostic lost its operation context.");
        Check.Equal(0, mapStrings.Count); Check.Equal(textCreated, textConsumed);
        Check.True(nativeLocks > 0, "Original lock implementation was not called.");
        foreach (var (name, value) in new[] { ("Room_Club", 2), ("Room_CircleRoom", 3), ("Room_Dormitory", 4),
            ("Room_NeoInnsmouth", 5), ("Room_NeoArkham", 6), ("Room_NeoDunwich", 7),
            ("Room_NeoRhodeIsland", 8), ("Room_Pool", 9), ("VR_Room_Club", 10),
            ("VR_Room_CircleRoom", 11), ("VR_Room_Dormitory", 12), ("VR_Room_NeoInnsmouth", 13),
            ("VR_Room_NeoArkham", 14), ("VR_Room_NeoDunwich", 15), ("VR_Room_NeoRhodeIsland", 16), ("VR_Room_Pool", 17) })
        {
            Check.True(CardAreaPolicy.TryGetArea(name, out byte area), "Known area was rejected.");
            Check.Equal((byte)value, area);
        }
    }

    [UnmanagedCallersOnly] private static nint HeadType() => Current.headClass;
    [UnmanagedCallersOnly] private static nint BodyType() => Current.bodyClass;
    [UnmanagedCallersOnly] private static nint FilterType() => Current.filterClass;
    [UnmanagedCallersOnly] private static nint RareType() => Current.rareClass;
    [UnmanagedCallersOnly] private static void NoResults(nint selector, byte empty) => Current.events.Add("no-results:" + empty);
    [UnmanagedCallersOnly] private static void Visibility(nint widget, byte value) => Current.visibility[widget] = value;
    [UnmanagedCallersOnly] private static void Style(nint widget, byte selected)
    { Current.events.Add("style"); Current.styles[widget] = selected; }
    [UnmanagedCallersOnly] private static CardFilterText* FromLiteral(CardFilterText* text, char* literal)
    {
        string value = new(literal);
        char* data = (char*)NativeMemory.Alloc((nuint)(value.Length + 1) * 2);
        value.AsSpan().CopyTo(new Span<char>(data, value.Length)); data[value.Length] = '\0';
        *text = new() { Data = (nint)data, Flags = 2 };
        Current.textCreated++;
        return text;
    }
    [UnmanagedCallersOnly] private static void Text(nint widget, CardFilterText* text)
    {
        Current.badges[widget] = new string((char*)text->Data);
        NativeMemory.Free((void*)text->Data);
        Current.textConsumed++;
    }
    [UnmanagedCallersOnly] private static nint Children(nint panel, CardFilterArray<nint>* output)
    {
        nint[] children = Current.panels[panel];
        nint* data = (nint*)NativeMemory.Alloc((nuint)Math.Max(1, children.Length) * 8);
        children.AsSpan().CopyTo(new Span<nint>(data, children.Length));
        Current.childArrays.Add((nint)data);
        *output = new() { Data = data, Count = children.Length, Capacity = children.Length };
        return (nint)output;
    }
    [UnmanagedCallersOnly] private static void FreeChildren(void* data)
    {
        if (Current.mapStrings.Remove((nint)data)) { NativeMemory.Free(data); return; }
        if (Current.eventArrays.Remove((nint)data)) { NativeMemory.Free(data); return; }
        if (!Current.childArrays.Remove((nint)data)) Environment.FailFast("Freed borrowed card/selection data.");
        NativeMemory.Free(data); Current.freedChildren++;
    }

    [UnmanagedCallersOnly] private static int OriginalLock(nint pose, byte unowned, byte experience)
    {
        Current.nativeLocks++;
        bool exhausted = experience == 0 && Current.cardStamina.Any(current => current <= 0);
        byte locked = (byte)(unowned | (exhausted ? 1 : 0)); // Native block after FatiguePatchRecipe's OR.
        *(byte*)(pose + B.PoseLocked) = locked;
        *(byte*)(pose + 0x4f1) = experience; *(byte*)(pose + 0x4f2) = unowned;
        Current.visibility[*(nint*)(pose + B.PoseLockGroup)] = (byte)(unowned != 0 ? 0 : 2);
        Current.visibility[*(nint*)(pose + B.PoseTiredGroup)] = (byte)(exhausted ? 4 : 1);
        return locked;
    }
    [UnmanagedCallersOnly] private static byte OriginalMatch(nint selector, nint card, nint context)
    {
        if (selector != Current.selector || card == 0 || context != 0x1234)
            Environment.FailFast("Card predicate arguments or borrowed context changed.");
        Current.nativeMatches++;
        return Current.nativeMatchResult;
    }
    [UnmanagedCallersOnly] private static GameString* MapName(GameString* output)
    {
        string value = Current.mapName;
        char* data = (char*)NativeMemory.Alloc((nuint)(value.Length + 1) * 2);
        value.AsSpan().CopyTo(new Span<char>(data, value.Length)); data[value.Length] = '\0';
        *output = new() { Data = data, Length = value.Length + 1, Capacity = value.Length + 1 };
        Current.mapStrings.Add((nint)data); Current.mapReads++;
        return output;
    }
    [UnmanagedCallersOnly] private static nint FindAreaFunction(nint type, ulong name, int includeSuper)
    {
        if (type != Current.foreignClass || name != AreaFunctionName || includeSuper != 1)
            Environment.FailFast("Wrong receiver, method name or inheritance scope for area icons.");
        return Current.missingAreaFunction ? 0 : AreaFunction;
    }
    [UnmanagedCallersOnly] private static void* AllocateEvent(nuint bytes, uint alignment)
    {
        void* data = NativeMemory.Alloc(bytes);
        Current.eventArrays.Add((nint)data);
        return data;
    }
    [UnmanagedCallersOnly] private static void AreaEvent(nint pose, nint function, CardFilterArray<byte>* areas)
    {
        if (function != AreaFunction || !Current.eventArrays.Contains((nint)areas->Data) ||
            (nint)areas->Data == Current.borrowedAreas || areas->Count != 1 || areas->Data[0] != 9)
            Environment.FailFast("Wrong function or borrowed/invalid area-icon parameters.");
        Current.areaTips++;
    }
    [UnmanagedCallersOnly] private static void Log(char* message) => Current.events.Add(new string(message));
}
