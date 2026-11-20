using System.Security.Cryptography;
using Dollhouse.Core.Security;
using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Asn1.X9;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;

namespace Dollhouse.Server.Licensing;

public sealed class LicenseDeviceKey
{
    private readonly ECPublicKeyParameters key;
    public string PublicKey { get; }
    public string Fingerprint { get; }

    private LicenseDeviceKey(ECPublicKeyParameters key)
    {
        this.key = key;
        var der = new SubjectPublicKeyInfo(new AlgorithmIdentifier(X9ObjectIdentifiers.IdECPublicKey,
            X9ObjectIdentifiers.Prime256v1), key.Q.Normalize().GetEncoded(false)).GetDerEncoded();
        PublicKey = LocalLicenseAuthority.Base64Url(der);
        Fingerprint = Convert.ToHexStringLower(SHA256.HashData(der));
    }

    public static LicenseDeviceKey Import(string encoded)
    {
        if (encoded.Length is < 1 or > 256) throw new CryptographicException("Invalid device public key.");
        var der = LocalLicenseAuthority.DecodeBase64Url(encoded);
        try
        {
            var spki = SubjectPublicKeyInfo.GetInstance(Asn1Object.FromByteArray(der));
            if (!spki.Algorithm.Algorithm.Equals(X9ObjectIdentifiers.IdECPublicKey)
                || spki.Algorithm.Parameters?.ToAsn1Object() is not DerObjectIdentifier curve
                || !curve.Equals(X9ObjectIdentifiers.Prime256v1)
                || PublicKeyFactory.CreateKey(spki) is not ECPublicKeyParameters key
                || key.Q.IsInfinity || !key.Q.IsValid())
                throw new CryptographicException("Expected a complete P-256 public key.");
            return new LicenseDeviceKey(key);
        }
        catch (Exception error) when (error is ArgumentException or IOException or InvalidOperationException)
        { throw new CryptographicException("Invalid device public key.", error); }
    }

    public bool Verify(byte[] message, byte[] signature)
    {
        try
        {
            var values = StandardDsaEncoding.Instance.Decode(key.Parameters.N, signature);
            var signer = new ECDsaSigner();
            signer.Init(false, key);
            return signer.VerifySignature(SHA256.HashData(message), values[0], values[1]);
        }
        catch (Exception error) when (error is ArgumentException or IOException or InvalidOperationException or InvalidCastException)
        { return false; }
    }
}
