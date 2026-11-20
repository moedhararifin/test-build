using Dollhouse.Core.Patching;
using Dollhouse.Core.Security;

namespace Dollhouse.Core.Startup;

public sealed class PreparedGame(GameInstallation installation, WorkspacePaths paths, string certificateSha256,
    LocalLicenseAuthority licenseAuthority) : IDisposable
{
    public GameInstallation Installation { get; } = installation;
    public WorkspacePaths Paths { get; } = paths;
    public string CertificateSha256 { get; } = certificateSha256;
    public LocalLicenseAuthority LicenseAuthority { get; } = licenseAuthority;
    internal FileStream? OriginalBinary { get; set; }

    internal FileStream TakeOriginalBinary()
    {
        var original = OriginalBinary ?? throw new InvalidOperationException("No verified game binary is available for launch.");
        OriginalBinary = null;
        return original;
    }

    public void Dispose() { OriginalBinary?.Dispose(); OriginalBinary = null; }
}

public static class GamePreparation
{
    public static PreparedGame Prepare(GameInstallation installation, Action<string>? progress = null, Action<string>? details = null,
        Action<string, string?>? operation = null, Action<BinaryProgress>? binaryProgress = null,
        bool retainVerifiedBinary = false)
    {
        progress?.Invoke("[1/4] Checking game...");
        operation?.Invoke("verify or restore the original game", installation.Executable);
        FileStream? original = GameBinary.PrepareAndOpenOriginal(installation.Executable, update =>
        {
            operation?.Invoke(update.Operation.ToLowerInvariant(), update.Path);
            binaryProgress?.Invoke(update);
            if (update.IsComplete)
                details?.Invoke($"Binary verification: {update.Operation} — {update.Elapsed.TotalSeconds:F2}s.");
        });
        try
        {
            if (!retainVerifiedBinary) { original.Dispose(); original = null; }
            var paths = installation.Paths;
            operation?.Invoke("check local Dollhouse data", paths.Local);
            var missingState = IsStateMissing(paths.Local);

            progress?.Invoke("[2/4] Preparing secure connection...");
            LocalLicenseAuthority? authority = null;
            var existingAuthority = File.Exists(LocalLicenseAuthority.StatePath(paths));
            if (!missingState)
            {
                // The disk EXE is original on every launcher run. Existing state,
                // rather than a patched trust literal, determines whether creation
                // is allowed. Refuse partial identities before creating TLS files.
                operation?.Invoke("verify existing local license-signing keys", LocalLicenseAuthority.StatePath(paths));
                authority = LocalLicenseAuthority.Setup(paths, allowCreate: false);
            }
            var existingCertificate = File.Exists(paths.Certificate);
            operation?.Invoke("prepare the HTTPS certificate and Windows TLS key", paths.Tls);
            var certificate = LocalCertificate.Setup(paths, progress, details);
            progress?.Invoke($"HTTPS certificate {(existingCertificate ? "verified" : "created")} (expires {certificate.NotAfter:yyyy-MM-dd}).");
            details?.Invoke($"Certificate: {certificate.Certificate}; private key and loopback names verified.");
            operation?.Invoke("prepare local license-signing keys", LocalLicenseAuthority.StatePath(paths));
            authority ??= LocalLicenseAuthority.Setup(paths, allowCreate: missingState);
            progress?.Invoke(existingAuthority ? "Local license issuer ready (saved signing keys verified)."
                : "Local license issuer created (new signing keys saved).");
            details?.Invoke($"License keys: {LocalLicenseAuthority.StatePath(paths)}; keyset and lease signing ready.");
            progress?.Invoke("[3/4] Configuring game...");
            operation?.Invoke("extract the game log configuration", GameLogConfiguration.PathFor(installation));
            var logChanged = GameLogConfiguration.Prepare(installation);
            progress?.Invoke($"Log filter {(logChanged ? "installed" : "verified")}.");
            operation?.Invoke("prepare cinematic camera presets", CameraConfiguration.PathFor(installation));
            var cameraCreated = CameraConfiguration.Prepare(installation);
            progress?.Invoke($"Camera presets {(cameraCreated ? "installed" : "preserved")}.");
            details?.Invoke($"Camera presets: {CameraConfiguration.PathFor(installation)}; existing customizations are preserved.");
            operation?.Invoke("prepare the game runtime", RuntimeCompatibility.LibraryPath(installation));
            RuntimeCompatibility.Prepare(installation);
            progress?.Invoke("Runtime ready.");
            details?.Invoke($"Server: {GameBuild.NewUrl}; original disk constants/imports preserved.");
            details?.Invoke($"Engine.ini: {GameLogConfiguration.PathFor(installation)}; selected by -EngineINI at launch.");
            return new(installation, paths, certificate.CertificateSha256, authority) { OriginalBinary = original };
        }
        catch { original?.Dispose(); throw; }
    }

    private static bool IsStateMissing(string path)
    {
        try
        {
            if (!File.GetAttributes(path).HasFlag(FileAttributes.Directory))
                throw new IOException($"Dollhouse data path is not a directory: {path}");
            return false;
        }
        catch (FileNotFoundException) { return true; }
        catch (DirectoryNotFoundException) { return true; }
    }
}
