using System.Net.Sockets;
using System.Security.Cryptography;
using Dollhouse.App;
using Dollhouse.Core.Security;
using Dollhouse.Core.Startup;

namespace Dollhouse.Tests;

internal static class StartupTests
{
    public static void Verify()
    {
        VerifyErrors();
        VerifyCertificates();
        VerifyPreparationContext();
    }

    private static void VerifyCertificates()
    {
        using var workspace = new TemporaryWorkspace();
        var paths = workspace.Paths;
        Directory.CreateDirectory(paths.Certificate); // Obstruct only the certificate destination.
        var error = Check.Throws<Exception>(() => LocalCertificate.Setup(paths));
        Check.True(error is IOException or UnauthorizedAccessException, "Expected a file-access failure.");
        Check.True(!File.Exists(paths.PrivateKey), "Failed setup left a newly created private key behind.");
        Directory.Delete(paths.Certificate);
        var certificate = LocalCertificate.Setup(paths);
        var key = File.ReadAllBytes(paths.PrivateKey);
        var report = Path.Combine(paths.Tls, "certificate.json");
        var written = File.GetLastWriteTimeUtc(report);
        LocalCertificate.Setup(paths);
        Check.Equal(written, File.GetLastWriteTimeUtc(report));
        File.Delete(report);
        Directory.CreateDirectory(report);
        var warnings = new List<string>();
        Check.Equal(certificate.CertificateSha256, LocalCertificate.Setup(paths, warnings.Add).CertificateSha256);
        Check.Equal(1, warnings.Count);
        Check.True(key.SequenceEqual(File.ReadAllBytes(paths.PrivateKey)), "Optional report failure rotated the key.");

        using var partial = new TemporaryWorkspace();
        Directory.CreateDirectory(partial.Paths.Tls);
        File.Copy(paths.Certificate, partial.Paths.Certificate);
        Check.Throws<InvalidOperationException>(() => LocalCertificate.Setup(partial.Paths));
        Check.True(!File.Exists(partial.Paths.PrivateKey), "Existing incomplete identity was silently replaced.");
        Check.True(File.ReadAllBytes(paths.Certificate).SequenceEqual(File.ReadAllBytes(partial.Paths.Certificate)),
            "Existing certificate changed after refusal.");

        File.WriteAllText(paths.PrivateKey, "invalid key fixture");
        Check.Throws<CryptographicException>(() => LocalCertificate.Setup(paths));
        Check.Equal("invalid key fixture", File.ReadAllText(paths.PrivateKey));
    }

    private static void VerifyPreparationContext()
    {
        using var invalid = new TemporaryWorkspace();
        var target = Path.Combine(invalid.Paths.Root, "unsupported.exe");
        File.WriteAllText(target, "not a supported game");
        string? operation = null, location = null;
        Check.Throws<InvalidDataException>(() => GamePreparation.Prepare(new(target, invalid.Paths.Root),
            operation: (name, path) => { operation = name; location = path; }));
        Check.Equal("verifying installed game", operation);
        Check.Equal(target, location);
        if (GameBinaryFixture.Original is { } fixture)
        {
            File.Copy(fixture, target, overwrite: true);
            var installation = new GameInstallation(target, invalid.Paths.Root);
            Directory.CreateDirectory(installation.Paths.Local); // Existing incomplete state must fail setup.
            Check.Throws<InvalidOperationException>(() => GamePreparation.Prepare(installation, retainVerifiedBinary: true));
            using var unlocked = File.Open(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Check.True(unlocked.CanWrite, "Failed preparation retained the verified EXE handle.");
        }
    }

    private static void VerifyErrors()
    {
        var denied = string.Join('\n', StartupErrors.Describe("prepare local license-signing keys", "test/license",
            new UnauthorizedAccessException("Denied"), false));
        Check.True(denied.Contains("license-signing") && denied.Contains("test/license") && denied.Contains("0x80070005"),
            "Startup error omitted the operation, target or Windows error code.");
        var socket = string.Join('\n', StartupErrors.Describe("check the local HTTPS port", "127.0.0.1:7900",
            new SocketException((int)SocketError.AccessDenied), false));
        Check.True(socket.Contains("AccessDenied") && !socket.Contains("already in use"),
            "Denied socket access was mislabeled as an occupied port.");
        var diskFull = string.Join('\n', StartupErrors.Describe("prepare backup", "test.exe",
            new IOException("Full", unchecked((int)0x80070070)), false));
        Check.True(diskFull.Contains("drive is full"), "Disk-full failure lacks a useful explanation.");
    }
}
