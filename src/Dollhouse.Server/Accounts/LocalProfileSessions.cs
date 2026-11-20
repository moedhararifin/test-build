using System.Security.Cryptography;
using System.Text.Json;
using Dollhouse.Core;

namespace Dollhouse.Server.Accounts;

public sealed record ProfileState(ProfileSummary Account, ProfileUpload Upload);
public sealed record ProfilePush(string Session, int Base, int Epoch, string Profile, Dictionary<string, string> Files,
    string? Kind = null, string[]? Conflicts = null);
public sealed record ProfileOperation(int Status, ProfileSummary? Account = null, string Reason = "",
    bool Kicked = false, string TookFrom = "", bool Released = false);

public sealed partial class LocalProfileStore
{
    public static bool ValidProfileSession(string? session) => session is { Length: >= 1 and <= 128 }
        && session.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.');

    public static bool ValidPush(ProfilePush? push) => push is not null && ValidProfileSession(push.Session)
        && push.Base >= 1 && push.Epoch >= 1 && push.Profile is { Length: >= 1 and <= 128 }
        && push.Profile.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-')
        && push.Kind is null or "upload" or "merge" && ValidFiles(push.Files)
        && (push.Conflicts is null || (push.Conflicts.Length <= 128 && push.Conflicts.All(value =>
            value is { Length: >= 1 and <= 128 } && !value.Any(char.IsControl))));

    public ProfileState? ReadState(string owner, string id)
    {
        lock (gate)
        {
            var snapshot = Find(owner, id);
            return snapshot is null ? null : new(Summary(snapshot), Copy(snapshot.Upload));
        }
    }

    public ProfileOperation Claim(string owner, string id, string session)
    {
        ValidateSession(session);
        lock (gate)
        {
            var snapshot = Find(owner, id);
            if (snapshot is null) return new(404, Reason: "not_found");
            if (snapshot.ActiveSession == session) return new(200, Summary(snapshot));
            var previous = snapshot.ActiveSession;
            // Epoch identifies the data generation, not the owning process. The
            // native no-change download path can skip rewriting local metadata.
            var updated = snapshot with { ActiveSession = session, ReleasedSession = null, LastPushFingerprint = null };
            Commit(updated);
            return new(200, Summary(updated), TookFrom: previous is null ? "" : "another local session");
        }
    }

    public ProfileOperation Heartbeat(string owner, string id, string session)
    {
        ValidateSession(session);
        lock (gate)
        {
            var snapshot = Find(owner, id);
            if (snapshot is null) return new(404, Reason: "not_found");
            // Resume the unclaimed v1 snapshots created before profile sessions
            // existed, or acquire a free profile after an idle/manual-sync release.
            // Neither case changes the epoch a running client already knows.
            if (snapshot.ActiveSession is null)
            {
                snapshot = snapshot with { ActiveSession = session, ReleasedSession = null };
                Commit(snapshot);
            }
            return snapshot.ActiveSession == session ? new(200, Summary(snapshot))
                : new(200, Summary(snapshot), Reason: "another_login", Kicked: true);
        }
    }

    public ProfileOperation Push(string owner, string id, ProfilePush push)
    {
        if (!ValidPush(push)) throw new ArgumentException("Invalid local profile push.");
        lock (gate)
        {
            var snapshot = Find(owner, id);
            if (snapshot is null) return new(404, Reason: "not_found");
            if (snapshot.ActiveSession != push.Session) return new(423, Summary(snapshot), "another_login");
            if (snapshot.Epoch != push.Epoch) return new(410, Summary(snapshot), "rollback");
            var fingerprint = Fingerprint(push);
            if (snapshot.LastPushFingerprint == fingerprint) return new(200, Summary(snapshot));
            if (snapshot.Rev != push.Base) return new(409, Summary(snapshot), "conflict");
            // Native upload/merge/automatic pushes all carry complete file maps.
            // The source identity stays stable even when imported on another client.
            var upload = snapshot.Upload with { Files = new(push.Files, StringComparer.Ordinal) };
            var updated = snapshot with { Upload = upload, Rev = checked(snapshot.Rev + 1), LastPushFingerprint = fingerprint };
            Commit(updated);
            return new(200, Summary(updated));
        }
    }

    public ProfileOperation Release(string owner, string id, string session)
    {
        ValidateSession(session);
        lock (gate)
        {
            var snapshot = Find(owner, id);
            if (snapshot is null) return new(404, Reason: "not_found");
            if (snapshot.ActiveSession != session)
                return new(200, Summary(snapshot), Released: snapshot.ActiveSession is null && snapshot.ReleasedSession == session);
            var updated = snapshot with { ActiveSession = null, ReleasedSession = session };
            Commit(updated);
            return new(200, Summary(updated), Released: true);
        }
    }

    private Snapshot? Find(string owner, string id) => snapshots.FirstOrDefault(item => item.Owner == owner && item.Id == id);
    private void Commit(Snapshot updated)
    {
        SaveSnapshot(updated, replace: true);
        snapshots[snapshots.FindIndex(item => item.Id == updated.Id)] = updated;
    }
    private static void ValidateSession(string session)
    {
        if (!ValidProfileSession(session)) throw new ArgumentException("Invalid profile session.", nameof(session));
    }
    private static string Fingerprint(ProfilePush push) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
    {
        push.Session, push.Base, push.Epoch, push.Profile, push.Kind,
        Files = new SortedDictionary<string, string>(push.Files, StringComparer.Ordinal),
        Conflicts = (push.Conflicts ?? []).Order(StringComparer.Ordinal).ToArray()
    }, JsonOutput.Options)));
}
