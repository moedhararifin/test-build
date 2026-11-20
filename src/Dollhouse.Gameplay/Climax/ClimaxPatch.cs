namespace Dollhouse.Gameplay;

/// <summary>Match UI_Sub_FemaleState.InitEcstasyPoint and BPL_HLibrary.IsCrush.</summary>
public static class ClimaxPatch
{
    public const float DefaultFemaleThreshold = 90f;

    public static float FemaleEcstasyThreshold(int relationshipLevel) =>
        relationshipLevel >= 5 ? 80f : DefaultFemaleThreshold;
}
