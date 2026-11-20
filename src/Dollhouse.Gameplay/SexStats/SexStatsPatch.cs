namespace Dollhouse.Gameplay;

/// <summary>Lifetime character statistics from structured ERoleAttr deltas.</summary>
public static class SexStatsPatch
{
    // Contiguous native ERoleAttr values: human/crossbreeding experience (58–59),
    // intercourse counts (60–62), orgasms/incontinence (63–64), internal lifetime
    // amounts (65–67), and external ejaculation count/amount (68–69).
    public const int FirstAttribute = 58;
    public const int LastAttribute = 69;

    // Lifetime totals have no body-capacity limit. Widen before adding so overflow
    // cannot wrap a large accumulated statistic to zero.
    public static int Accumulate(int current, int delta) =>
        (int)Math.Clamp((long)current + delta, 0, int.MaxValue);
}
