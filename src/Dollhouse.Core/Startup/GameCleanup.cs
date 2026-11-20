using Dollhouse.Core.Patching;

namespace Dollhouse.Core.Startup;

public sealed record CleanupTarget(string Path, bool Directory);

public static class GameCleanup
{
    public static void Run(GameInstallation installation, Action<string>? progress = null)
    {
        installation.EnsureNotRunning();
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var targets = Plan(installation, localAppData, Environment.GetEnvironmentVariable("FALLENDOLL_PAK_CACHE"));
        progress?.Invoke("Deleting saves, settings, local accounts, profile history, debug captures and update caches.");
        progress?.Invoke("Shared Windows PAK/license caches and the game's device-signing key will also be reset.");
        RestoreAndDelete(installation, targets, progress);
        if (OperatingSystem.IsWindows()) WindowsDeviceIdentity.Delete(progress);
        progress?.Invoke("Original game executable verified; redundant canonical backup removed. Installed content preserved; local identities removed.");
        progress?.Invoke("No game-owned registry key is verified; Windows MachineGuid and shared engine/driver registry data are preserved.");
    }

    public static IReadOnlyList<CleanupTarget> Plan(GameInstallation installation, string localAppData, string? pakCacheOverride = null)
    {
        if (!Path.IsPathFullyQualified(localAppData))
            throw new InvalidOperationException("Cannot locate the current user's local application data.");
        localAppData = Path.GetFullPath(localAppData);
        var root = Path.GetFullPath(installation.Root);
        var targets = new List<CleanupTarget>
        {
            new(installation.SavedDirectory, true),
            new(Path.Combine(localAppData, GameInstallation.ProjectName, "Saved"), true),
            new(Path.Combine(localAppData, "FallenDollRehost"), true)
        };
        foreach (var folder in new[] { "auth", "profiles", "debug", "updates", "tls", "license", "config", "device", "runtime" })
            targets.Add(new(Path.Combine(installation.Paths.Local, folder), true));
        targets.Add(new(RuntimeCompatibility.PreviousLibraryPath(installation), false));
        targets.Add(new(Path.Combine(installation.Paths.Local, "runtime.log"), false));
        targets.Add(new(Path.Combine(installation.Paths.Local, "settings.json"), false));
        if (!string.IsNullOrWhiteSpace(pakCacheOverride))
        {
            // The override is a single cache file, never a directory or arbitrary installed file.
            var cache = Path.GetFullPath(pakCacheOverride);
            if (!Path.GetExtension(cache).Equals(".bin", StringComparison.OrdinalIgnoreCase)
                || Within(cache, Path.Combine(root, "Engine"))
                || Within(cache, Path.Combine(installation.ProjectDirectory, "Content"))
                || Within(cache, Path.Combine(installation.ProjectDirectory, "Binaries"))
                || Within(cache, installation.Paths.Tls)
                || Within(cache, Path.Combine(installation.Paths.Local, "license")))
                throw new InvalidOperationException("FALLENDOLL_PAK_CACHE must name a .bin cache file outside installed content, binaries and TLS/signing identity directories. Nothing was deleted.");
            if (!targets.Any(target => Within(cache, target.Path))) targets.Add(new(cache, false));
        }
        return targets.DistinctBy(target => target.Path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static void RestoreAndDelete(GameInstallation installation, IReadOnlyList<CleanupTarget> targets,
        Action<string>? progress = null)
    {
        // Complete path validation before modifying the EXE or deleting data.
        ValidatePath(installation.Executable, false);
        var backup = GameBinary.BackupPath(installation.Executable);
        if (File.Exists(backup)) ValidatePath(backup, false);
        foreach (var target in targets) ValidateTree(target.Path, target.Directory);
        GameBinary.PrepareOriginal(installation.Executable);
        progress?.Invoke("Verified original game executable; redundant canonical backup removed. Manual copies preserved.");
        DeleteTargets(targets, progress);
        // Explicit cleanup must leave a genuinely absent state folder so a later
        // first-time setup is distinct from an existing empty/corrupt identity.
        if (Directory.Exists(installation.Paths.Local))
        {
            ValidatePath(installation.Paths.Local, true);
            if (Directory.GetFileSystemEntries(installation.Paths.Local).Length == 0)
                Directory.Delete(installation.Paths.Local);
        }
    }

    public static void DeleteTargets(IReadOnlyList<CleanupTarget> targets, Action<string>? progress = null)
    {
        // Inspect all targets before the first mutation. Never follow junctions/symlinks.
        foreach (var target in targets) ValidateTree(target.Path, target.Directory);
        foreach (var target in targets)
        {
            try
            {
                if (!Exists(target.Path))
                {
                    progress?.Invoke($"Already absent: {target.Path}");
                    continue;
                }
                DeleteTree(target.Path, target.Directory);
                progress?.Invoke($"Deleted: {target.Path}");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                throw new IOException($"Cleanup stopped at {target.Path}: {error.Message} Earlier reported deletions have already completed.", error);
            }
        }
    }

    private static bool Within(string path, string directory) =>
        path.Equals(directory, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    private static bool Exists(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static void ValidatePath(string path, bool directory)
    {
        if (!Path.IsPathFullyQualified(path))
            throw new IOException("Refusing an unbounded cleanup path.");
        var fullPath = Path.GetFullPath(path);
        if (!string.Equals(path, fullPath, StringComparison.OrdinalIgnoreCase)
            || Path.TrimEndingDirectorySeparator(fullPath) == Path.TrimEndingDirectorySeparator(Path.GetPathRoot(fullPath)!))
            throw new IOException("Refusing a noncanonical or unbounded cleanup path.");
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
            if (Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"Refusing cleanup through a junction or symbolic link: {current}");
        if (Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.Directory) != directory)
            throw new IOException($"Cleanup target has an unexpected file type: {path}");
    }

    private static void ValidateTree(string path, bool directory)
    {
        ValidatePath(path, directory);
        if (!directory || !Exists(path)) return;
        foreach (var child in Directory.EnumerateFileSystemEntries(path))
            ValidateTree(child, File.GetAttributes(child).HasFlag(FileAttributes.Directory));
    }

    private static void DeleteTree(string path, bool directory)
    {
        ValidatePath(path, directory);
        if (!directory) { File.Delete(path); return; }
        foreach (var child in Directory.EnumerateFileSystemEntries(path))
            DeleteTree(child, File.GetAttributes(child).HasFlag(FileAttributes.Directory));
        Directory.Delete(path);
    }
}
