namespace Dollhouse.Core.Patching;

/// <summary>Identity and original connection literals of the supported standalone build.</summary>
public static class GameBuild
{
    public const string OriginalSha256 = "7d96feb44c8426d80d2098ead8f6e2301a278ca914d3ebd340617fe742eccfa4";
    public const int ExpectedSize = 191_914_496;
    public const int UrlOffset = 0x09668478;
    public const int PinOffset = 0x096684b0;
    public const int LicenseRootOffset = 0x09667320;
    public const string OldLicenseRoot = "KRUFwogq8ylo9pia5R7GR1n5R-Ejm_-v3XyAIyJqRdI=";
    public const string OldUrl = "https://62.106.75.243:7900";
    public const string NewUrl = "https://127.0.0.1:7900";
    public const string OldPin = "ac036ff79041e8919f2f9420502212f0723cc217b0593c8982c450c33572b10b";
}
