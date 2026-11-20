#if DOLLHOUSE_PATCH_BINDINGS
namespace Dollhouse.Core.Patching;
#else
namespace Dollhouse.Gameplay;
#endif

// Shared by memory-patch recipes and the Native AOT runtime: export names are one contract.
internal static class GameplayLibrary
{
    public const string UseItem = "DollhouseUseItem";
    public const string ReflectedUseItem = "DollhouseReflectedUseItem";
    public const string ItemUseStock = "DollhouseItemUseStock";
    public const string ItemUseCompleted = "DollhouseItemUseCompleted";
    public const string FemaleThreshold = "DollhouseFemaleEcstasyThreshold";
    public const string FemaleThresholdFunction = "DollhouseFemaleThresholdFunction";
    public const string GenericFemaleThreshold = "DollhouseGenericFemaleThreshold";
    public const string ExecFemaleThreshold = "DollhouseExecFemaleThreshold";
    public const string PersistCumflationWithContext = "DollhousePersistCumflationWithContext";
    public const string NotifyCumflationCleared = "DollhouseNotifyCumflationCleared";
    public const string TickCumflation = "DollhouseTickCumflation";
    public const string CaptureDrain = "DollhouseCaptureDrain";
    public const string DispatchDrainCleared = "DollhouseDispatchDrainCleared";
    public const string ShowDrainRightMsg = "DollhouseShowDrainRightMsg";
    public const string SemenCapacity = "DollhouseSemenCapacity";
    public const string DispatchBulgeEligibility = "DollhouseDispatchBulgeEligibility";
    // Stable published name; Shared/ScriptStartup now initializes both script repairs.
    public const string InstallCameraLoader = "DollhouseInstallCameraLoader";
    public const string RefreshCardFilters = "DollhouseRefreshCardFilters";
    public const string SetCardLock = "DollhouseSetCardLock";
    public const string MatchesCardFilters = "DollhouseMatchesCardFilters";
    public const string DrinkStamina = "DollhouseDrinkStamina";
    public const string StaminaProgressionMode = "DollhouseStaminaProgressionMode";
}
