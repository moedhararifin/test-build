using System.Security.Cryptography;
using System.Text.Json;
using Org.BouncyCastle.Asn1.Sec;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Utilities;

namespace Dollhouse.DeviceCrypto;

/// <summary>Per-game software identity. Never imports or changes Windows device keys.</summary>
internal sealed class DeviceKeyStore(string file)
{
    private static readonly ECDomainParameters Domain = new(SecNamedCurves.GetByName("secp256r1"));

    internal ECPrivateKeyParameters? Read()
    {
        var bytes = ReadFile();
        if (bytes is null) return null;
        try { return ParseKey(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private byte[]? ReadFile()
    {
        if (!Path.IsPathFullyQualified(file)) throw new IOException("Device key path must be absolute.");
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > 1024) throw new InvalidDataException("Device key file is invalid.");
            var bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
            return bytes;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static ECPrivateKeyParameters ParseKey(byte[] bytes)
    {
        using var json = JsonDocument.Parse(bytes);
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 2
            || root.GetProperty("version").GetInt32() != 1)
            throw new InvalidDataException("Device key file is invalid.");
        var scalar = root.GetProperty("private_key").GetBytesFromBase64();
        try
        {
            var d = new BigInteger(1, scalar);
            if (scalar.Length != 32 || d.SignValue <= 0 || d.CompareTo(Domain.N) >= 0)
                throw new InvalidDataException("Device key file is invalid.");
            return new ECPrivateKeyParameters(d, Domain);
        }
        finally { CryptographicOperations.ZeroMemory(scalar); }
    }

    internal static ECPrivateKeyParameters Create()
    {
        var generator = new ECKeyPairGenerator();
        generator.Init(new ECKeyGenerationParameters(Domain, new SecureRandom()));
        return (ECPrivateKeyParameters)generator.GenerateKeyPair().Private;
    }

    internal ECPrivateKeyParameters Commit(ECPrivateKeyParameters key)
    {
        // A concurrent creator wins without replacement; use its complete identity.
        if (Read() is { } existing) return existing;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            WriteKey(temporary, key);
            try { File.Move(temporary, file, overwrite: false); }
            catch (IOException) when (File.Exists(file))
            { return Read() ?? throw new IOException("Committed device identity disappeared."); }
            return key;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void WriteKey(string path, ECPrivateKeyParameters key)
    {
        var scalar = BigIntegers.AsUnsignedByteArray(32, key.D);
        try
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WriteNumber("version", 1);
                writer.WriteBase64String("private_key", scalar);
                writer.WriteEndObject();
            }
            stream.Flush(flushToDisk: true);
        }
        finally { CryptographicOperations.ZeroMemory(scalar); }
    }
}
