using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Net.Mail;
using Dollhouse.Core;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;

namespace Dollhouse.Server.Accounts;

public sealed record LocalUser(string Name, string Id);
public sealed record LocalLogin(LocalUser User, string Session);

/// <summary>Private per-game accounts, provisioned by their first local login.</summary>
public sealed class LocalAccountStore
{
    private const int Iterations = 600_000;
    private readonly object gate = new();
    private readonly string file;
    private readonly Dictionary<string, Account> accounts = new(StringComparer.OrdinalIgnoreCase);

    private sealed record Account(string Name, string Id, string Salt, string PasswordHash, string Session, string Email = "");
    private sealed record State(int Version, Account[] Accounts);

    public LocalAccountStore(WorkspacePaths paths)
    {
        var directory = Path.Combine(paths.Local, "auth");
        Directory.CreateDirectory(directory);
        file = Path.Combine(directory, "accounts.json");
        if (!File.Exists(file)) return;
        try
        {
            var state = JsonSerializer.Deserialize<State>(File.ReadAllBytes(file), JsonOutput.Options);
            if (state is null || state.Version != 1 || state.Accounts is null) throw new InvalidDataException();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var sessions = new HashSet<string>(StringComparer.Ordinal);
            foreach (var account in state.Accounts)
            {
                if (account is null || !ValidName(account.Name) || account.Name != NormalizeName(account.Name)
                    || !Guid.TryParseExact(account.Id, "N", out _) || !ids.Add(account.Id)
                    || Convert.FromBase64String(account.Salt).Length != 16
                    || Convert.FromBase64String(account.PasswordHash).Length != 32
                    || account.Session is not { Length: 64 } || Convert.FromHexString(account.Session).Length != 32
                    || (account.Email != "" && !ValidEmail(account.Email))
                    || !sessions.Add(account.Session) || !accounts.TryAdd(account.Name, account))
                    throw new InvalidDataException();
            }
        }
        catch (Exception error) when (error is JsonException or FormatException or InvalidDataException or ArgumentException)
        {
            // Do not include serializer diagnostics, passwords, hashes, or tokens in console errors.
            throw new InvalidDataException("Local account data is invalid. Preserve .dollhouse/auth/accounts.json for recovery; it was not overwritten.");
        }
    }

    public static string NormalizeName(string name) => name.Trim().Normalize(NormalizationForm.FormC);
    public static bool ValidName(string? name) => name is not null && name.Length is >= 1 and <= 64
        && !string.IsNullOrWhiteSpace(name) && !name.Any(character => char.IsControl(character) || char.IsSurrogate(character));

    public LocalLogin? Login(string name, string password)
    {
        name = NormalizeName(name);
        if (!ValidName(name) || password.Length is < 1 or > 1024) throw new ArgumentException("Invalid local login input.");
        lock (gate)
        {
            if (accounts.TryGetValue(name, out var account))
            {
                var computed = HashPassword(password, Convert.FromBase64String(account.Salt));
                try
                {
                    if (!CryptographicOperations.FixedTimeEquals(computed, Convert.FromBase64String(account.PasswordHash))) return null;
                }
                finally { CryptographicOperations.ZeroMemory(computed); }
                return ToLogin(account);
            }

            var salt = RandomNumberGenerator.GetBytes(16);
            var hash = HashPassword(password, salt);
            try
            {
                account = new Account(name, Guid.NewGuid().ToString("N"), Convert.ToBase64String(salt),
                    Convert.ToBase64String(hash), Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)));
            }
            finally { CryptographicOperations.ZeroMemory(hash); }
            // Publish in-memory state only after the private file is committed successfully.
            Save([.. accounts.Values, account]);
            accounts.Add(name, account);
            return ToLogin(account);
        }
    }

    private static byte[] HashPassword(string password, byte[] salt)
    {
        // Keep the v1 PBKDF2 format without relying on Windows CNG support in Wine.
        var bytes = Encoding.UTF8.GetBytes(password);
        try
        {
            var generator = new Pkcs5S2ParametersGenerator(new Sha256Digest());
            generator.Init(bytes, salt, Iterations);
            return ((KeyParameter)generator.GenerateDerivedMacParameters(256)).GetKey();
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public LocalLogin? FindSession(string? session)
    {
        if (string.IsNullOrEmpty(session) || session.Length != 64) return null;
        lock (gate)
        {
            var account = accounts.Values.FirstOrDefault(value => value.Session == session);
            return account is null ? null : ToLogin(account);
        }
    }

    public static bool ValidEmail(string? email) => email is not null && email.Length is >= 3 and <= 254
        && email == email.Trim() && !email.Any(char.IsControl)
        && MailAddress.TryCreate(email, out var parsed) && parsed.Address == email;

    public string? GetEmail(string session)
    {
        lock (gate) return accounts.Values.FirstOrDefault(account => account.Session == session)?.Email;
    }

    public bool SetEmail(string session, string email)
    {
        if (!ValidEmail(email)) throw new ArgumentException("Invalid local email address.", nameof(email));
        lock (gate)
        {
            var account = accounts.Values.FirstOrDefault(account => account.Session == session);
            if (account is null) return false;
            if (account.Email == email) return true;
            var updated = account with { Email = email };
            Save(accounts.Values.Select(value => value.Id == account.Id ? updated : value).ToArray());
            accounts[account.Name] = updated;
            return true;
        }
    }

    private static LocalLogin ToLogin(Account account) => new(new LocalUser(account.Name, account.Id), account.Session);

    private void Save(Account[] records)
    {
        var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new State(1, records), JsonOutput.Options);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, file, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
