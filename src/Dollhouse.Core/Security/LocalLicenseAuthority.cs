using System.Security.Cryptography;
using System.Text.Json;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace Dollhouse.Core.Security;

public sealed record SignedEnvelope(int Version, string KeyId, string Signed, string Signature);

/// <summary>Persistent local signing identity, independent of HTTPS and archive encryption.</summary>
public sealed class LocalLicenseAuthority
{
    public const string RootKeyId = "root-2026";
    public const string SigningKeyId = "dollhouse-local-1";
    private sealed record State(int Version, string RootSeed, string SigningSeed, long Sequence,
        long IssuedAt, long NotBefore, long NotAfter);
    public string RootPublicKey { get; }
    public string SigningPublicKey { get; }
    public SignedEnvelope Keyset { get; }
    public long SigningNotBefore { get; }
    public long SigningNotAfter { get; }
    private readonly Ed25519PrivateKeyParameters signingKey;

    private LocalLicenseAuthority(State state)
    {
        var rootSeed = Convert.FromBase64String(state.RootSeed);
        var signingSeed = Convert.FromBase64String(state.SigningSeed);
        try
        {
            if (rootSeed.Length != 32 || signingSeed.Length != 32 || rootSeed.AsSpan().SequenceEqual(signingSeed)
                || state.Version != 1 || state.Sequence <= 0 || state.IssuedAt <= 0
                || state.NotBefore > state.IssuedAt || state.NotBefore < 0 || state.NotAfter <= state.IssuedAt)
                throw new InvalidDataException();
            var root = new Ed25519PrivateKeyParameters(rootSeed);
            var signing = new Ed25519PrivateKeyParameters(signingSeed);
            signingKey = signing;
            SigningNotBefore = state.NotBefore;
            SigningNotAfter = state.NotAfter;
            RootPublicKey = Base64Url(root.GeneratePublicKey().GetEncoded());
            SigningPublicKey = Base64Url(signing.GeneratePublicKey().GetEncoded());
            var payload = JsonSerializer.SerializeToUtf8Bytes(new
            {
                Type = "keyset", Product = "fallendoll", KeyId = RootKeyId,
                Seq = state.Sequence, state.IssuedAt,
                Keys = new[] { new { KeyId = SigningKeyId, Public = SigningPublicKey, state.NotBefore, state.NotAfter } },
                Revoked = Array.Empty<string>()
            }, JsonOutput.Options);
            var signer = new Ed25519Signer();
            signer.Init(true, root);
            signer.BlockUpdate(payload, 0, payload.Length);
            Keyset = new SignedEnvelope(3, RootKeyId, Base64Url(payload), Base64Url(signer.GenerateSignature()));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(rootSeed);
            CryptographicOperations.ZeroMemory(signingSeed);
        }
    }

    public static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_');
    public SignedEnvelope SignLease<T>(T payload, long issuedAt)
    {
        if (issuedAt < SigningNotBefore || issuedAt >= SigningNotAfter)
            throw new InvalidOperationException("Local signing key is outside its validity interval.");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOutput.Options);
        var signer = new Ed25519Signer();
        signer.Init(true, signingKey);
        signer.BlockUpdate(bytes, 0, bytes.Length);
        return new SignedEnvelope(3, SigningKeyId, Base64Url(bytes), Base64Url(signer.GenerateSignature()));
    }

    public static byte[] DecodeBase64Url(string value) => Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/'));
    public static string StatePath(WorkspacePaths paths) => Path.Combine(paths.Local, "license", "authority.json");

    public static LocalLicenseAuthority Setup(WorkspacePaths paths, bool allowCreate = true, TimeProvider? clock = null)
    {
        var file = StatePath(paths);
        byte[]? saved;
        try { saved = File.ReadAllBytes(file); }
        catch (FileNotFoundException) { saved = null; }
        catch (DirectoryNotFoundException) { saved = null; }
        if (saved is not null)
        {
            try
            {
                var state = JsonSerializer.Deserialize<State>(saved, JsonOutput.Options);
                return new LocalLicenseAuthority(state ?? throw new InvalidDataException());
            }
            catch (Exception error) when (error is JsonException or FormatException or ArgumentException or InvalidDataException)
            {
                throw new InvalidDataException("Local signing identity is invalid. Preserve .dollhouse/license/authority.json for recovery; it was not overwritten.");
            }
            finally { CryptographicOperations.ZeroMemory(saved); }
        }
        if (!allowCreate)
            throw new InvalidOperationException("This game already trusts a local signing identity, but .dollhouse/license/authority.json is missing. Restore that file from a backup. Existing Dollhouse data was not reset.");

        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        var generated = new State(1, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), now.ToUnixTimeMilliseconds(),
            now.ToUnixTimeSeconds(), now.AddMinutes(-5).ToUnixTimeSeconds(), now.AddYears(10).ToUnixTimeSeconds());
        var authority = new LocalLicenseAuthority(generated);
        var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(JsonSerializer.SerializeToUtf8Bytes(generated, JsonOutput.Options));
                stream.Flush(flushToDisk: true);
            }
            // Never replace an existing identity, including a concurrent creator's file.
            File.Move(temporary, file, overwrite: false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return authority;
    }
}
