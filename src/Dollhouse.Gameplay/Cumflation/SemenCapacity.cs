namespace Dollhouse.Gameplay;

/// <summary>Shared womb/GI capacity in millilitres, using the user's character table.</summary>
public static class SemenCapacity
{
    // These are native role keys, not translated display labels. Compare without case
    // so ADA/Ada and YanShi/Yanshi share the same policy.
    private static readonly (string Role, int Millilitres)[] Characters =
    [
        ("Erika", 3300),
        ("Galatea", 2100),
        ("Talon", 3600),
        ("Alet", 2600),
        ("Anya", 4200),
        ("Celia", 3500),
        ("Juzi", 3400),
        ("Elizabeth", 3600),
        ("YanShi", 2500),
        ("ADA", 4300)
    ];

    public static int ForRole(ReadOnlySpan<char> role)
    {
        foreach (var character in Characters)
            if (role.Equals(character.Role, StringComparison.OrdinalIgnoreCase)) return character.Millilitres;
        return SupportedBuild.SemenCapacityMl; // Preserve the previous policy for unknown roles.
    }

    internal static unsafe int Read(GameString* role)
    {
        if (role == null || role->Data == null || role->Length <= 0 || role->Length > role->Capacity)
            return SupportedBuild.SemenCapacityMl;
        var key = new ReadOnlySpan<char>(role->Data, role->Length);
        if (key[^1] == '\0') key = key[..^1];
        return ForRole(key); // Borrow only for this call; no allocation or saved UObject pointer.
    }
}
