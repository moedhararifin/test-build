using System.Buffers.Binary;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Utilities;

namespace Dollhouse.DeviceCrypto;

/// <summary>One P-256 identity and its native public-key/signature encodings.</summary>
internal sealed class DeviceSigningKey(DeviceKeyStore store, ECPrivateKeyParameters material, bool finalized)
{
    public const int DigestSize = 32;
    public const int SignatureSize = 64;
    public const int PublicBlobSize = 72;
    private ECPrivateKeyParameters privateKey = material;
    public bool IsFinalized { get; private set; } = finalized;

    public void FinalizeKey()
    {
        privateKey = store.Commit(privateKey);
        IsFinalized = true;
    }

    public byte[] ExportPublicBlob()
    {
        var blob = new byte[PublicBlobSize];
        BinaryPrimitives.WriteUInt32LittleEndian(blob, 0x31534345); // ECS1: ECDSA public P-256
        BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(4), 32);
        var point = privateKey.Parameters.G.Multiply(privateKey.D).Normalize().GetEncoded(false);
        point.AsSpan(1).CopyTo(blob.AsSpan(8)); // Omit the uncompressed-point prefix.
        return blob;
    }

    public byte[] SignHash(ReadOnlySpan<byte> digest)
    {
        var signer = new ECDsaSigner(new HMacDsaKCalculator(new Sha256Digest()));
        signer.Init(true, privateKey);
        var values = signer.GenerateSignature(digest.ToArray()); // Input is already SHA-256.
        var signature = new byte[SignatureSize];
        BigIntegers.AsUnsignedByteArray(32, values[0]).CopyTo(signature, 0);
        BigIntegers.AsUnsignedByteArray(32, values[1]).CopyTo(signature, 32);
        return signature;
    }
}
