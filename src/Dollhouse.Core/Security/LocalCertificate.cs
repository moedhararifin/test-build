using System.Net;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Dollhouse.Core.Security;

public sealed record CertificateReport(string Certificate, string PrivateKey, string CertificateSha256,
    DateTimeOffset NotBefore, DateTimeOffset NotAfter, string[] San, bool SystemTrustModified = false);

public static class LocalCertificate
{
    public static string Fingerprint(X509Certificate2 certificate) =>
        certificate.GetCertHashString(HashAlgorithmName.SHA256).ToLowerInvariant();

    public static string Fingerprint(string path)
    {
        using var certificate = X509CertificateLoader.LoadCertificateFromFile(path);
        return Fingerprint(certificate);
    }

    public static X509Certificate2 Load(WorkspacePaths paths, Action<string>? details = null)
    {
        details?.Invoke("TLS: loading the certificate and private key.");
        var certificate = OperatingSystem.IsWindows()
            ? LoadCryptoApiCertificate(paths, details)
            : X509Certificate2.CreateFromPemFile(paths.Certificate, paths.PrivateKey);
        try
        {
            details?.Invoke("TLS: checking certificate validity and private-key availability.");
            var now = DateTime.UtcNow;
            if (!certificate.HasPrivateKey || now < certificate.NotBefore.ToUniversalTime()
                || now >= certificate.NotAfter.ToUniversalTime())
                throw new InvalidOperationException("Certificate/key is missing or outside its validity interval.");
            if (OperatingSystem.IsWindows())
            {
                // GetRSAPrivateKey may reopen even a CAPI-bound certificate through
                // CNG. Use the CAPI key we already own; Proton can crash in that getter.
                using var publicKey = CreateCryptoApiKey();
                publicKey.ImportRSAPublicKey(certificate.PublicKey.EncodedKeyValue.RawData, out _);
                VerifyKey(((CryptoApiCertificate)certificate).Rsa, publicKey, details);
            }
            else
            {
                using var key = certificate.GetRSAPrivateKey()
                    ?? throw new InvalidOperationException("An RSA certificate is required.");
                using var publicKey = certificate.GetRSAPublicKey()!;
                VerifyKey(key, publicKey, details);
            }
            return certificate;
        }
        catch
        {
            certificate.Dispose();
            throw;
        }
    }

    private static void VerifyKey(RSA key, RSA publicKey, Action<string>? details)
    {
        var proof = "Dollhouse certificate/key check"u8;
        details?.Invoke("TLS: signing the validation proof with the loaded RSA provider.");
        var signature = key.SignData(proof, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        details?.Invoke("TLS: verifying the proof against the certificate's public key.");
        if (!publicKey.VerifyData(proof, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
            throw new InvalidOperationException("Certificate and private key do not match.");
    }

    [SupportedOSPlatform("windows")]
    private static RSACryptoServiceProvider CreateCryptoApiKey() => new(2048,
        new CspParameters(24) // PROV_RSA_AES supports RSA-2048 with SHA-256.
        {
            KeyContainerName = "Dollhouse-TLS-" + Guid.NewGuid().ToString("N"),
            KeyNumber = (int)KeyNumber.Exchange,
            Flags = CspProviderFlags.NoPrompt
        }) { PersistKeyInCsp = false };

    [SupportedOSPlatform("windows")]
    private static X509Certificate2 LoadCryptoApiCertificate(WorkspacePaths paths, Action<string>? details)
    {
        details?.Invoke("TLS: parsing the public PEM certificate.");
        using var publicCertificate = X509Certificate2.CreateFromPem(File.ReadAllText(paths.Certificate));
        details?.Invoke("TLS: creating the temporary CryptoAPI RSA container.");
        var key = CreateCryptoApiKey();
        try
        {
            details?.Invoke("TLS: importing the existing PEM private key into CryptoAPI.");
            try { key.ImportFromPem(File.ReadAllText(paths.PrivateKey)); }
            catch (ArgumentException error)
            { throw new CryptographicException("The TLS private key is not a supported RSA PEM key.", error); }
            details?.Invoke("TLS: attaching the CryptoAPI private key to the certificate.");
            using var paired = publicCertificate.CopyWithPrivateKey(key);
            details?.Invoke("TLS: retaining the certificate and its CryptoAPI container.");
            return new CryptoApiCertificate(paired, key);
        }
        catch { key.Dispose(); throw; }
    }

    // Schannel opens this named CAPI key while serving HTTPS. Keep it alive with
    // the certificate, then delete the temporary container on certificate disposal.
    [SupportedOSPlatform("windows")]
    private sealed class CryptoApiCertificate(X509Certificate2 certificate, RSACryptoServiceProvider key)
        : X509Certificate2(certificate)
    {
        public RSACryptoServiceProvider Rsa => key;

        protected override void Dispose(bool disposing)
        {
            try { base.Dispose(disposing); }
            finally { if (disposing) key.Dispose(); }
        }
    }

    public static CertificateReport Setup(WorkspacePaths paths, Action<string>? warning = null, Action<string>? details = null)
    {
        details?.Invoke("TLS: checking the TLS directory and existing PEM files.");
        Directory.CreateDirectory(paths.Tls);
        var hasCertificate = File.Exists(paths.Certificate);
        if (hasCertificate != File.Exists(paths.PrivateKey))
            throw new InvalidOperationException("Incomplete TLS material; refusing to overwrite it.");
        if (!hasCertificate) CreateIdentity(paths, details);
        using var certificate = Load(paths, details);
        details?.Invoke("TLS: checking certificate names.");
        var extension = certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single();
        var names = extension.EnumerateDnsNames().Concat(extension.EnumerateIPAddresses().Select(x => x.ToString())).ToArray();
        if (!new[] { "localhost", "127.0.0.1", "::1" }.All(names.Contains))
            throw new InvalidOperationException("Certificate must name localhost and both loopback addresses.");
        var report = new CertificateReport(paths.Certificate, paths.PrivateKey, Fingerprint(certificate),
            certificate.NotBefore.ToUniversalTime(), certificate.NotAfter.ToUniversalTime(), names);
        details?.Invoke("TLS: updating the optional certificate report.");
        var reportPath = Path.Combine(paths.Tls, "certificate.json");
        try
        {
            var json = JsonOutput.Serialize(report) + Environment.NewLine;
            if (!File.Exists(reportPath) || File.ReadAllText(reportPath) != json)
                File.WriteAllText(reportPath, json);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            warning?.Invoke($"Could not save the optional certificate report at {reportPath} ({error.GetType().Name}, 0x{error.HResult:X8}). HTTPS identity is ready.");
        }
        details?.Invoke("TLS: setup validated; releasing its temporary certificate/key container.");
        return report;
    }

    private static void CreateIdentity(WorkspacePaths paths, Action<string>? details)
    {
        details?.Invoke("TLS: generating the RSA private key.");
        using RSA key = OperatingSystem.IsWindows() ? CreateCryptoApiKey() : RSA.Create(2048);
        details?.Invoke("TLS: building the certificate request and extensions.");
        var request = new CertificateRequest("CN=Dollhouse local research", key,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1")], false));
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        san.AddIpAddress(IPAddress.IPv6Loopback);
        request.CertificateExtensions.Add(san.Build());
        var now = DateTimeOffset.UtcNow;
        details?.Invoke("TLS: creating the self-signed certificate.");
        using var certificate = request.CreateSelfSigned(now.AddMinutes(-5), now.AddDays(365));
        var createdFiles = new List<string>();
        try
        {
            details?.Invoke("TLS: exporting and saving the new private key and certificate PEM files.");
            WritePem(paths.PrivateKey, key.ExportPkcs8PrivateKeyPem());
            WritePem(paths.Certificate, certificate.ExportCertificatePem());
            details?.Invoke("TLS: new PEM files saved; releasing the generated certificate/key container.");
        }
        catch
        {
            // Preserve existing identities and the original error if cleanup is denied.
            foreach (var path in createdFiles.AsEnumerable().Reverse())
            {
                try { File.Delete(path); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
            throw;
        }

        void WritePem(string path, string pem)
        {
            using var output = new FileStream(path, FileMode.CreateNew);
            createdFiles.Add(path);
            using var writer = new StreamWriter(output, leaveOpen: true);
            writer.Write(pem);
            writer.Flush();
            output.Flush(flushToDisk: true);
        }
    }
}
