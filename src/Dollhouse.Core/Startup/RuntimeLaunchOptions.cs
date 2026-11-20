namespace Dollhouse.Core.Startup;

internal static class RuntimeLaunchOptions
{
    internal const string NoGameplayPatchesVariable = "DOLLHOUSE_NO_GAMEPLAY_PATCHES";

    // Child-only launch choice, not persisted settings or a game command-line argument.
    internal static bool ParseNoGameplayPatches(string? value) => value switch
    {
        null or "0" => false,
        "1" => true,
        _ => throw new InvalidDataException($"{NoGameplayPatchesVariable} must be 0 or 1.")
    };
}
