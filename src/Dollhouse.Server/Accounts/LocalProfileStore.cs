using System.Text.Json;
using Dollhouse.Core;

namespace Dollhouse.Server.Accounts;

public sealed record ProfileSummary(string Id, string Name, long Created, int Rev, int Epoch);
public sealed record ProfileUpload(string Name, long Created, string Profile, Dictionary<string, string> Files);
public sealed record ProfileCreation(ProfileSummary Account, bool Conflict);

/// <summary>Private, durable profile snapshots and session ownership; separate from login identities.</summary>
public sealed partial class LocalProfileStore
{
    private sealed record Snapshot(int Version, string Owner, string Id, ProfileUpload Upload, int Rev, int Epoch,
        string? ActiveSession = null, string? ReleasedSession = null, string? LastPushFingerprint = null);
    private readonly object gate = new();
    private readonly string directory;
    private readonly List<Snapshot> snapshots = [];

    public LocalProfileStore(WorkspacePaths paths)
    {
        directory = Path.Combine(paths.Local, "profiles");
        Directory.CreateDirectory(directory);
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
            {
                var snapshot = JsonSerializer.Deserialize<Snapshot>(File.ReadAllBytes(file), JsonOutput.Options);
                if (snapshot is null || snapshot.Version != 1 || !Guid.TryParseExact(snapshot.Id, "N", out _)
                    || Path.GetFileNameWithoutExtension(file) != snapshot.Id || !Guid.TryParseExact(snapshot.Owner, "N", out _)
                    || snapshot.Rev < 1 || snapshot.Epoch < 1 || !ValidUpload(snapshot.Upload)
                    || (snapshot.ActiveSession is not null && !ValidProfileSession(snapshot.ActiveSession))
                    || (snapshot.ReleasedSession is not null && !ValidProfileSession(snapshot.ReleasedSession))
                    || (snapshot.LastPushFingerprint is not null && (snapshot.LastPushFingerprint.Length != 64
                        || snapshot.LastPushFingerprint.Any(character => !char.IsAsciiHexDigitLower(character))))
                    || snapshots.Any(item => item.Id == snapshot.Id || SameSource(item, snapshot.Owner, snapshot.Upload)))
                    throw new InvalidDataException();
                snapshots.Add(snapshot);
            }
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidDataException)
        {
            throw new InvalidDataException("Local profile data is invalid. Preserve .dollhouse/profiles for recovery; nothing was overwritten.");
        }
    }

    public static bool ValidUpload(ProfileUpload? upload)
    {
        if (upload is null || string.IsNullOrWhiteSpace(upload.Name) || upload.Name.Length > 128 || upload.Name.Any(char.IsControl)
            || upload.Created < 0 || upload.Created > 253402300799L
            || string.IsNullOrEmpty(upload.Profile) || upload.Profile.Length > 128
            || upload.Profile.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-')) return false;
        return ValidFiles(upload.Files);
    }

    public static bool ValidFiles(IReadOnlyDictionary<string, string>? files)
    {
        if (files is null || files.Count > 128) return false;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in files)
        {
            if (string.IsNullOrEmpty(name) || name.Length > 128 || name.Any(character => char.IsControl(character)
                    || character is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|')
                || name.Contains("..", StringComparison.Ordinal) || !name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
                || !names.Add(name) || value is null || value.Length > 8 * 1024 * 1024) return false;
            // Validate only the transport encoding; preserve file bytes as opaque
            // base64. Never decrypt, execute, or materialize uploaded filenames.
            if (!Convert.TryFromBase64String(value, new byte[value.Length], out _)) return false;
        }
        return true;
    }

    public ProfileSummary[] List(string owner)
    {
        lock (gate) return snapshots.Where(item => item.Owner == owner).Select(Summary).ToArray();
    }

    public ProfileUpload? Read(string owner, string id)
    {
        lock (gate)
        {
            var snapshot = snapshots.FirstOrDefault(item => item.Owner == owner && item.Id == id);
            return snapshot is null ? null : Copy(snapshot.Upload);
        }
    }

    public ProfileCreation Create(string owner, ProfileUpload upload)
    {
        if (!Guid.TryParseExact(owner, "N", out _) || !ValidUpload(upload)) throw new ArgumentException("Invalid local profile input.");
        lock (gate)
        {
            var existing = snapshots.FirstOrDefault(item => SameSource(item, owner, upload));
            if (existing is not null)
            {
                var same = existing.Upload.Name == upload.Name && existing.Upload.Files.Count == upload.Files.Count
                    && existing.Upload.Files.All(file => upload.Files.TryGetValue(file.Key, out var value) && file.Value == value);
                return new(Summary(existing), Conflict: !same);
            }
            var snapshot = new Snapshot(1, owner, Guid.NewGuid().ToString("N"), Copy(upload), 1, 1);
            SaveSnapshot(snapshot, replace: false);
            snapshots.Add(snapshot);
            return new(Summary(snapshot), Conflict: false);
        }
    }

    private void SaveSnapshot(Snapshot snapshot, bool replace)
    {
        var file = Path.Combine(directory, snapshot.Id + ".json");
        var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOutput.Options);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            if (replace) ArchiveSnapshot(file, snapshot.Id);
            File.Move(temporary, file, overwrite: replace);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private void ArchiveSnapshot(string file, string id)
    {
        var history = Path.Combine(directory, "history");
        Directory.CreateDirectory(history);
        var archive = Path.Combine(history, id + "-" + Guid.NewGuid().ToString("N") + ".json");
        var temporary = archive + ".tmp";
        // Finish the history copy before overwriting the current snapshot, including on exFAT.
        try
        {
            using (var previous = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var backup = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                previous.CopyTo(backup);
                backup.Flush(flushToDisk: true);
            }
            File.Move(temporary, archive, overwrite: false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static bool SameSource(Snapshot item, string owner, ProfileUpload upload) =>
        item.Owner == owner && item.Upload.Profile == upload.Profile && item.Upload.Created == upload.Created;
    private static ProfileSummary Summary(Snapshot item) => new(item.Id, item.Upload.Name, item.Upload.Created, item.Rev, item.Epoch);
    private static ProfileUpload Copy(ProfileUpload upload) => upload with { Files = new(upload.Files, StringComparer.Ordinal) };
}
