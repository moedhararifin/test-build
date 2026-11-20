using System.Security.Cryptography;
using Dollhouse.Core;
using Dollhouse.Core.Security;
using Dollhouse.Core.Startup;

namespace Dollhouse.Tests;

internal static class RuntimeTrustTests
{
    internal static void Verify()
    {
        using var workspace = new TemporaryWorkspace();
        var paths = workspace.Paths;
        // Disposable test identities only. The production reader never calls Setup.
        var certificate = LocalCertificate.Setup(paths);
        var authority = LocalLicenseAuthority.Setup(paths);
        var before = Directory.GetFiles(paths.Local, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, File.ReadAllBytes);
        var expected = new RuntimeTrust(certificate.CertificateSha256, authority.RootPublicKey);
        Check.Equal(expected, RuntimeTrust.Read(paths));
        Check.Equal(expected, RuntimeTrust.Read(paths));
        Check.Equal(before.Count, Directory.GetFiles(paths.Local, "*", SearchOption.AllDirectories).Length);
        foreach (var file in before)
            Check.True(file.Value.AsSpan().SequenceEqual(File.ReadAllBytes(file.Key)), "Trust reader modified an identity file.");
        Check.True(!File.Exists(Path.Combine(paths.Local, "runtime.bin")), "Trust loading must not create a configuration file.");

        // The game needs only the public certificate, not its private TLS key.
        File.Delete(paths.PrivateKey);
        Check.Equal(expected, RuntimeTrust.Read(paths));
        Check.True(!File.Exists(paths.PrivateKey), "Reader must not regenerate the TLS key.");

        var authorityPath = LocalLicenseAuthority.StatePath(paths);
        foreach (var invalid in new[] { "{}", "{", "[]",
            "{\"version\":1,\"root_seed\":\"invalid\"}",
            "{\"version\":1,\"root_seed\":\"AA==\"}",
            "{\"version\":2,\"root_seed\":\"AA==\"}",
            "{\"version\":1,\"version\":1,\"root_seed\":\"AA==\"}" })
        {
            File.WriteAllText(authorityPath, invalid);
            Check.Throws<InvalidDataException>(() => RuntimeTrust.Read(paths));
            Check.Equal(invalid, File.ReadAllText(authorityPath));
        }
        File.Delete(authorityPath);
        Check.Throws<FileNotFoundException>(() => RuntimeTrust.Read(paths));
        Check.True(!File.Exists(authorityPath), "Reader must not regenerate the signing identity.");
        File.WriteAllBytes(authorityPath, before[authorityPath]);

        File.WriteAllText(paths.Certificate, "invalid certificate");
        Check.Throws<CryptographicException>(() => RuntimeTrust.Read(paths));
        Check.Equal("invalid certificate", File.ReadAllText(paths.Certificate));
        File.Delete(paths.Certificate);
        Check.Throws<CryptographicException>(() => RuntimeTrust.Read(paths));
        Check.True(!File.Exists(paths.Certificate), "Reader must not regenerate a certificate.");

        var absent = WorkspacePaths.ForGame(Path.Combine(paths.Root, "absent-game"));
        Check.Throws<CryptographicException>(() => RuntimeTrust.Read(absent));
        Check.True(!Directory.Exists(absent.Root), "Reader must not create an installation or state folder.");
    }
}
