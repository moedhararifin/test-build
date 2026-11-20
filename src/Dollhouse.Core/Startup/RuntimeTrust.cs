using System.Security.Cryptography;
using System.Text.Json;
using Dollhouse.Core.Security;
using Org.BouncyCastle.Math.EC.Rfc8032;

namespace Dollhouse.Core.Startup;

/// <summary>Public trust values read from existing identities. Never creates or writes files.</summary>
public sealed record RuntimeTrust(string CertificatePin, string LicenseRoot)
{
    public static RuntimeTrust Read(WorkspacePaths paths) => new(
        LocalCertificate.Fingerprint(paths.Certificate), ReadPublicRoot(paths));

    private static string ReadPublicRoot(WorkspacePaths paths)
    {
        var saved = File.ReadAllBytes(LocalLicenseAuthority.StatePath(paths));
        byte[]? seed = null;
        try
        {
            // Parse the caller-owned buffer so it can be cleared after the document
            // is disposed. Decode only the root seed; never import the signing key.
            using var document = JsonDocument.Parse(saved.AsMemory());
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid signing identity.");
            var names = new HashSet<string>();
            foreach (var property in root.EnumerateObject())
                if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate signing identity field.");
            if (!root.TryGetProperty("version", out var version) || !version.TryGetInt32(out int number) || number != 1
                || !root.TryGetProperty("root_seed", out var encoded) || encoded.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("Missing or invalid signing root.");
            seed = encoded.GetBytesFromBase64();
            if (seed.Length != Ed25519.SecretKeySize) throw new InvalidDataException("Invalid signing root size.");
            var publicKey = new byte[Ed25519.PublicKeySize];
            Ed25519.GeneratePublicKey(seed, 0, publicKey, 0);
            return LocalLicenseAuthority.Base64Url(publicKey);
        }
        catch (Exception error) when (error is JsonException or FormatException or InvalidOperationException)
        {
            throw new InvalidDataException("Existing signing identity is invalid. Its files were not changed.", error);
        }
        finally
        {
            if (seed is not null) CryptographicOperations.ZeroMemory(seed);
            CryptographicOperations.ZeroMemory(saved);
        }
    }
}
