#if DOLLHOUSE_PATCH_BINDINGS
namespace Dollhouse.Core.Patching;
#else
namespace Dollhouse.Gameplay;
#endif

// Standalone SHA-256 7d96feb44c8426d80d2098ead8f6e2301a278ca914d3ebd340617fe742eccfa4.
// These are native Game-tab extensions, separate from F_GameSetting's script enum/save schema.
internal static class SettingsBindings
{
    public const string PathVariable = "DOLLHOUSE_GAMEPLAY_SETTINGS";
    public const string BuildExport = "DollhouseBuildSettingsMenu";
    public const string ReadRowExport = "DollhouseReadSettingsRow";
    public const string DefaultExport = "DollhouseDefaultSetting";
    public const string SetExport = "DollhouseSetSetting";
    public const string EffectiveExport = "DollhouseEffectiveSetting";
    public const string ApplyExport = "DollhouseApplySettings";
    public const string CumflationKey = "DollhouseCumflation";
    public const string CumflationLabel = "Cumflation";
    public const string IgnoreRestrictionsKey = "DollhouseIgnoreCumflationRestrictions";
    public const string IgnoreRestrictionsLabel = "Ignore cumflation restrictions";
    public const string FatigueKey = "DollhouseFatigue";
    public const string FatigueLabel = "Fatigue";
    public const int AddedRowCount = 3;
    public const int OriginalRowCount = 14;
    public const int RowSize = 24; // const TCHAR* key, const TCHAR* label, byte kind/restart + padding.
    public const int NativeRows = 0xac0ed40; // Mutable DATA: key pointers are initialized during native startup.
    public const int Build = 0x5537840; // void(UUserWidget* menu). Creates/owns native receivers and UMG rows.
    public const int BuildCall = 0x553d3af; // Menu discovery calls Build after the widget is constructed.
    public const int RowCountCheck = 0x5538a06; // cmp ecx,14 in Build's descriptor loop.
    public const int ReadRow = 0x55376f0; // int(const descriptor*). Committed selection index.
    public const int BuildReadCall = 0x5537f35;
    public const int RefreshReadCall = 0x553bad6;
    public const int Default = 0x55309c0; // FString*(out FString*, const TCHAR* key); owned result.
    public const int ResetDefaultCall = 0x553b4fc;
    public const int Set = 0x5532ef0; // void(const TCHAR* key, const FString* value); borrowed arguments.
    public const int ApplySetCall = 0x553a413;
    public const int Effective = 0x5530fe0; // FString*(out FString*, const TCHAR* key); owned result.
    public const int ApplyEffectiveCall = 0x553a50a;
    public const int Apply = 0x553a260; // void(URehostSettingsMenuHook*); commits pending receiver values.
    public const int ApplyExecTail = 0x53cfaf5; // Existing exec wrapper finishes FFrame stepping before this JMP.
    public const int Refresh = 0x553ba70; // void(URehostSettingsMenuHook*); restores committed indexes + row paint.
}
