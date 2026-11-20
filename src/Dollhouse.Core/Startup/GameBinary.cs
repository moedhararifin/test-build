using System.Security.Cryptography;
using Dollhouse.Core.Patching;

namespace Dollhouse.Core.Startup;

public static class GameBinary
{
    private const int BufferSize = 1024 * 1024;
    public static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    public static string BackupPath(string target) => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(target))!,
        Path.GetFileNameWithoutExtension(target) + ".backup" + Path.GetExtension(target));
    private static FileStream OpenBinary(string path) => new(path, FileMode.Open, FileAccess.Read,
        FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
    /// <summary>Verify an original EXE, or consume a verified old canonical backup once.</summary>
    public static void PrepareOriginal(string target, Action<BinaryProgress>? progress = null)
    {
        using var original = PrepareAndOpenOriginal(target, progress);
    }

    /// <summary>Prepare the original and keep its verified handle locked for launch.</summary>
    public static FileStream PrepareAndOpenOriginal(string target, Action<BinaryProgress>? progress = null)
    {
        target = Path.GetFullPath(target);
        var backup = BackupPath(target);
        using var status = new BinaryProgressReporter(progress);
        FileStream? original = null;
        try
        {
            if (File.Exists(backup))
            {
                // Hold the verified source open through copying. Never reopen an unverified source.
                using (var source = OpenVerifiedOriginal(backup, status, "Verifying previous-version backup"))
                {
                    original = TryOpenOriginal(target, status);
                    if (original == null)
                    {
                        RejectLink(target);
                        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
                        try
                        {
                            source.Position = 0;
                            status.Begin("Restoring original game", target, GameBuild.ExpectedSize);
                            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                            {
                                var buffer = new byte[BufferSize];
                                int count;
                                while ((count = source.Read(buffer)) != 0)
                                { output.Write(buffer, 0, count); status.Advance(count); }
                                output.Flush(flushToDisk: true);
                            }
                            status.Complete();
                            using (OpenVerifiedOriginal(temporary, status, "Verifying restored copy")) { }
                            File.Move(temporary, target, overwrite: true);
                        }
                        finally { if (File.Exists(temporary)) File.Delete(temporary); }
                    }
                    // Keep the old backup until the installed EXE passes independent verification.
                    original ??= OpenVerifiedOriginal(target, status, "Verifying restored game");
                }
                status.Begin("Removing redundant binary backup", backup);
                File.Delete(backup);
                status.Complete();
            }
            else
            {
                original = OpenVerifiedOriginal(target, status, "Verifying installed game");
            }
            return original;
        }
        catch { original?.Dispose(); throw; }
    }

    // The returned read-only handle prevents replacement/writes during launch preparation.
    public static FileStream OpenVerifiedOriginal(string path) => OpenVerifiedOriginal(path, null, "Verifying installed game");

    private static FileStream OpenVerifiedOriginal(string path, BinaryProgressReporter? status, string operation)
    {
        RejectLink(path);
        var stream = OpenBinary(path);
        try
        {
            status?.Begin(operation, path, stream.Length);
            if (stream.Length != GameBuild.ExpectedSize || StreamHash(stream, status) != GameBuild.OriginalSha256)
                throw new InvalidDataException($"Not the supported original game binary: {path}. Restore the original game files and try again.");
            status?.Complete();
            stream.Position = 0;
            return stream;
        }
        catch { stream.Dispose(); throw; }
    }

    private static FileStream? TryOpenOriginal(string path, BinaryProgressReporter status)
    {
        if (!File.Exists(path)) return null;
        RejectLink(path);
        var stream = OpenBinary(path);
        try
        {
            status.Begin("Verifying installed game", path, stream.Length);
            bool original = stream.Length == GameBuild.ExpectedSize && StreamHash(stream, status) == GameBuild.OriginalSha256;
            status.Complete();
            if (original) { stream.Position = 0; return stream; }
            stream.Dispose();
            return null;
        }
        catch { stream.Dispose(); throw; }
    }

    private static string StreamHash(Stream stream, BinaryProgressReporter? status)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[BufferSize];
        int count;
        while ((count = stream.Read(buffer)) != 0)
        { hash.AppendData(buffer, 0, count); status?.Advance(count); }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static void RejectLink(string path)
    {
        if (File.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            throw new IOException($"Refusing a linked game binary: {path}");
    }
}
