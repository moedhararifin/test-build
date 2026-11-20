using System.Net.Security;
using System.Runtime.Versioning;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Dollhouse.Core;
using Dollhouse.Core.Security;

namespace Dollhouse.Tests;

internal static class CertificateProviderTests
{
    public static void Verify()
    {
        using var fresh = new TemporaryWorkspace();
        LocalCertificate.Setup(fresh.Paths);
        VerifyLoadedCertificate(fresh.Paths);

        // The previous implementation generated RSA keys through the platform default.
        using var existing = new TemporaryWorkspace();
        Directory.CreateDirectory(existing.Paths.Tls);
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Existing TLS fixture", key,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(System.Net.IPAddress.Loopback);
        san.AddIpAddress(System.Net.IPAddress.IPv6Loopback);
        request.CertificateExtensions.Add(san.Build());
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        File.WriteAllText(existing.Paths.Certificate, certificate.ExportCertificatePem());
        File.WriteAllText(existing.Paths.PrivateKey, key.ExportPkcs8PrivateKeyPem());
        var pem = File.ReadAllBytes(existing.Paths.Certificate);
        var privatePem = File.ReadAllBytes(existing.Paths.PrivateKey);
        Check.Equal(LocalCertificate.Fingerprint(certificate), LocalCertificate.Setup(existing.Paths).CertificateSha256);
        VerifyLoadedCertificate(existing.Paths);
        Check.True(pem.SequenceEqual(File.ReadAllBytes(existing.Paths.Certificate)), "Provider change rewrote the certificate/pin.");
        Check.True(privatePem.SequenceEqual(File.ReadAllBytes(existing.Paths.PrivateKey)), "Provider change rotated the private key.");

        using var otherKey = RSA.Create(2048);
        File.WriteAllText(existing.Paths.PrivateKey, otherKey.ExportPkcs8PrivateKeyPem());
        var mismatch = Check.Throws<Exception>(() => LocalCertificate.Load(existing.Paths));
        Check.True(mismatch is ArgumentException or CryptographicException, "Expected a mismatched RSA key rejection.");
        Check.True(pem.SequenceEqual(File.ReadAllBytes(existing.Paths.Certificate)), "Mismatched-key refusal changed the certificate.");
    }

    private static void VerifyLoadedCertificate(WorkspacePaths paths)
    {
        using var first = LocalCertificate.Load(paths);
        using var second = LocalCertificate.Load(paths);
        if (!OperatingSystem.IsWindows())
        {
            using var key = first.GetRSAPrivateKey()!;
            using var publicKey = first.GetRSAPublicKey()!;
            var proof = "TLS provider regression"u8;
            Check.True(publicKey.VerifyData(proof, key.SignData(proof, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1),
                HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1), "Loaded key cannot sign for its certificate.");
        }
        var context = SslStreamCertificateContext.Create(first, additionalCertificates: null, offline: true);
        Check.Equal(first.Thumbprint, context.TargetCertificate.Thumbprint);
        if (OperatingSystem.IsWindows()) VerifyCryptoApiLifetime(first, second);
    }

    [SupportedOSPlatform("windows")]
    private static void VerifyCryptoApiLifetime(X509Certificate2 first, X509Certificate2 second)
    {
        var parameters = ReadProvider(first);
        Check.Equal(24, parameters.ProviderType);
        Check.True(parameters.KeyContainerName!.StartsWith("Dollhouse-TLS-", StringComparison.Ordinal),
            "Certificate is not bound to its temporary CryptoAPI container.");
        using (var key = new RSACryptoServiceProvider(parameters))
        {
            _ = key.SignData("CryptoAPI signing"u8, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        first.Dispose();
        Check.Throws<CryptographicException>(() =>
        {
            using var stale = new RSACryptoServiceProvider(parameters);
            stale.PersistKeyInCsp = false;
        });
        using var remaining = new RSACryptoServiceProvider(ReadProvider(second));
        _ = remaining.SignData("Other certificate remains usable"u8, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    }

    [SupportedOSPlatform("windows")]
    private static CspParameters ReadProvider(X509Certificate2 certificate)
    {
        var size = 0;
        Check.True(CertGetCertificateContextProperty(certificate.Handle, 2, IntPtr.Zero, ref size), "No certificate key-provider binding.");
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Check.True(CertGetCertificateContextProperty(certificate.Handle, 2, buffer, ref size), "Cannot read certificate key provider.");
            var info = Marshal.PtrToStructure<KeyProviderInfo>(buffer);
            return new CspParameters(info.ProviderType, Marshal.PtrToStringUni(info.ProviderName), Marshal.PtrToStringUni(info.ContainerName))
            {
                KeyNumber = info.KeySpec,
                Flags = CspProviderFlags.UseExistingKey | CspProviderFlags.NoPrompt
            };
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyProviderInfo
    {
        public IntPtr ContainerName, ProviderName;
        public int ProviderType, Flags, ParameterCount;
        public IntPtr Parameters;
        public int KeySpec;
    }

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CertGetCertificateContextProperty(IntPtr certificate, int property, IntPtr data, ref int size);
}
