using System.Security.Cryptography;
using Dollhouse.Core;
using Dollhouse.Core.Patching;
using Dollhouse.Core.Startup;

namespace Dollhouse.Tests;

/// <summary>Optional original game bytes, supplied explicitly and never executed.</summary>
internal static class GameBinaryFixture
{
    private static readonly Lazy<string?> original = new(FindOriginal);
    public static string? Original => original.Value;

    private static string? FindOriginal()
    {
        var configured = Environment.GetEnvironmentVariable("DOLLHOUSE_TEST_GAME");
        var explicitPath = !string.IsNullOrWhiteSpace(configured);
        var path = explicitPath ? Path.GetFullPath(configured!)
            : Path.Combine(WorkspacePaths.Discover().Local, "fixtures", GameInstallation.ShippingName);
        if (!File.Exists(path))
        {
            if (explicitPath) throw new FileNotFoundException("DOLLHOUSE_TEST_GAME does not name an existing original game binary.", path);
            return null;
        }
        using var file = File.OpenRead(path);
        if (Convert.ToHexStringLower(SHA256.HashData(file)) != GameBuild.OriginalSha256)
            throw new InvalidDataException("The game test fixture is not the supported original binary.");
        return path;
    }
}
