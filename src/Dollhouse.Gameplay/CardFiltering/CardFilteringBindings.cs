#if DOLLHOUSE_PATCH_BINDINGS
namespace Dollhouse.Core.Patching;
#else
namespace Dollhouse.Gameplay;
#endif

// Supported standalone 7d96feb4… only. See docs/investigation/card-filtering.md.
// These are native UI layouts, not the Steam selector's different member offsets.
internal static class CardFilteringBindings
{
    // Replace the no-results submission after the matching-card array is complete.
    // R14 = selector; [RSP+48h] = borrowed TArray<const FHM_CardData*>.
    // The adapter preserves ShowNoFounded before refreshing the left-side widgets.
    public const int RefreshTail = 0x542093d;
    public const int RefreshTailEnd = 0x542094d;
    public const int MatchingCardsOnStack = 0x48;
    public const int ShowNoFounded = 0x53dbcb0; // void(selector*, bool).
    // Shared refresh's predicate call, before the matching-card array append.
    // byte(selector*, const FHM_CardData*, borrowed native ownership context*).
    // Keep the original predicate for ownership, rarity, search and other filters;
    // the compiled adapter first requires ALL selected values in each multi-select.
    public const int MatchCardCall = 0x5420218;
    public const int OriginalCardMatches = 0x541ef40;
    // Native member-call relay, reached by card creation, refresh and stamina
    // updates. Keep the underlying ownership/stamina implementation intact.
    public const int CardLockRelay = 0x53db9a0;
    public const int OriginalCardLock = 0x543c1a0; // int(pose*, bool unowned, bool experience).
    public const int CurrentMapName = 0x53673a0; // FString*(out owned FString*); world-derived, no server request.
    // FName value initialized from L"SetUsableInIcon" at 0x1019670/0x1019677.
    public const int UsableAreasName = 0xaff6c08;
    // UFunction*(UClass*, FName by value, includeSuper). Nullable counterpart
    // called by FindFunctionChecked at 0x15bf12c; we never use its fatal wrapper.
    public const int FindWidgetFunction = 0x14bcbe0;
    public const int ProcessEventSlot = 0x260;

    public const int PanelChildren = 0x36a1500; // TArray<UWidget*>*(panel*, out array*); caller frees array.Data.
    public const int HeadClass = 0x53d1520; // USelectMoodHeadUIBase::StaticClass().
    public const int BodyClass = 0x53d87d0; // USelectMoodXPUIBase::StaticClass().
    public const int FilterClass = 0x53d1d80; // USelectMoodFilterUIBase::StaticClass().
    public const int RareClass = 0x53d7fd0; // USelectMoodRareUIBase::StaticClass().
    public const int FilterSelected = 0x53d5a50; // void(widget*, bool); dispatches existing script style.
    public const int RareSelected = 0x53dba80; // void(widget*, bool); dispatches existing script style.
    // FText*(out FText*, const TCHAR*). Creates a native owned 16-byte FText.
    // UTextBlock::SetText takes it BY VALUE and releases it, as in Steam's badge setter.
    public const int TextFromLiteral = 0x129f9a0;
    public const int VisibilitySlot = 0x2c0;
    public const int TextSlot = 0x358;

    public const int SelectedFemales = 0x328, SelectedMales = 0x338;
    public const int SelectedOrgans = 0x348, SelectedConditions = 0x358;
    public const int SelectedState = 0x368, SelectedSmile = 0x369, SelectedRare = 0x36c;
    public const int FemalePanel = 0x3b8, MalePanel = 0x3c0;
    public const int OrganPanel = 0x3c8, ConditionPanel = 0x3d0;
    public const int StatePanel = 0x3d8, SmilePanel = 0x3e0, RarePanel = 0x3e8;

    // FHM_CardData's byte-enum arrays. Each paired group contributes BOTH arrays.
    public const int CardCharacters = 0x28;
    public const int CardOrgans = 0x40, CardOtherOrgans = 0x50;
    public const int CardConditions = 0x60, CardOtherConditions = 0x70;
    public const int CardLocation = 0x90, CardAreas = 0x180;
    public const int PoseData = 0x360, PoseLocked = 0x4f0;
    public const int PoseTiredGroup = 0x330, PoseLockGroup = 0x338;
    public const int PoseLockReason = 0x348, PoseUsableAreas = 0x350;

    // Shared child controls; these offsets match both reference and supported build.
    public const int EnableIcon = 0x338, Selection = 0x340;
    public const int FemaleGroup = 0x348, MaleGroup = 0x350, BadgeText = 0x358;
    public const int HeadCharacter = 0x36c, HeadIsFemale = 0x36d;
    public const int HeadEnabled = 0x398, HeadSelected = 0x399;
    // XPData stores distinct enums: Genre at +8 (body part), Theme at +9 (fetish).
    public const int OrganValue = 0x350, ConditionValue = 0x351;
    public const int BodyEnabled = 0x380, BodySelected = 0x381;
    public const int FilterState = 0x340, FilterSmile = 0x341, FilterSelectedFlag = 0x381;
    public const int RareValue = 0x340, RareSelectedFlag = 0x391;
}
