namespace Dollhouse.Gameplay;

internal sealed unsafe partial class NativeGame
{
    internal float FemaleEcstasyThreshold(GameString* role)
    {
        if (role == null || role->Data == null || role->Length <= 1)
            return ClimaxPatch.DefaultFemaleThreshold;
        int experience = 0, level = 0;
        ((delegate* unmanaged<GameString*, int*, int*, void>)At(SupportedBuild.RoleFavor))(role, &experience, &level);
        return ClimaxPatch.FemaleEcstasyThreshold(level);
    }

    internal void CompleteThresholdExec(GameString* role, nint frame, float* result)
    {
        // The original wrapper already parsed/allocated RoleName. Finish its FFrame and free that string.
        ((ScriptFrame*)frame)->FinishArguments();
        try { *result = FemaleEcstasyThreshold(role); }
        finally { Free(role); }
    }
}
