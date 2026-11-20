using System.Security.Cryptography;
using Dollhouse.Core.Patching;
using Dollhouse.Core.Security;
using Dollhouse.Core.Startup;

namespace Dollhouse.Tests;

internal static class MissingStateRecoveryTests
{
    public static void Verify()
    {
        if (GameBinaryFixture.Original is not { } fixture
            || !RuntimeCompatibility.IsBundled) return;
        using var workspace = new TemporaryWorkspace();
        var game = new GameInstallation(Path.Combine(workspace.Paths.Root, "game.exe"), workspace.Paths.Root);
        File.Copy(fixture, game.Executable);
        Directory.CreateDirectory(game.SavedDirectory);
        var save = Path.Combine(game.SavedDirectory, "player.sav");
        File.WriteAllText(save, "existing game save: preserve exactly");
        var before = GamePreparation.Prepare(game);
        var backup = GameBinary.BackupPath(game.Executable);
        var oldRoot = before.LicenseAuthority.RootPublicKey;
        var oldCertificate = before.CertificateSha256;
        var patchedHash = Hash(game.Executable);
        MoveState(game, "previous-state"); // Simulate copying an install without its hidden data.

        // A missing folder alone never authorizes accepting invalid game/backup bytes.
        File.WriteAllText(backup, "invalid backup fixture");
        RefusesMissingState(game, patchedHash);
        Check.Equal("invalid backup fixture", File.ReadAllText(backup));
        File.Delete(backup);
        File.WriteAllText(game.Executable, "modified fixture without a canonical backup");
        RefusesMissingState(game, Hash(game.Executable));
        Check.True(!File.Exists(backup), "Missing backup was reconstructed from a patched game.");
        File.Copy(fixture, game.Executable, overwrite: true);
        File.Copy(fixture, backup);
        FlipFirstByte(game.Executable);

        var logs = new List<string>();
        var recovered = GamePreparation.Prepare(game, logs.Add);
        Check.True(logs.Any(line => line.Contains("issuer created")), "Fresh identities were not explained.");
        Check.True(recovered.LicenseAuthority.RootPublicKey != oldRoot, "Missing issuer was not recreated.");
        Check.True(recovered.CertificateSha256 != oldCertificate, "Missing TLS identity was not recreated.");
        Check.Equal(recovered.LicenseAuthority.RootPublicKey, RuntimeTrust.Read(game.Paths).LicenseRoot);
        Check.True(!File.Exists(backup), "Verified restoration retained its old backup.");
        Check.Equal(GameBuild.OriginalSha256, Hash(game.Executable));
        Check.Equal("existing game save: preserve exactly", File.ReadAllText(save));
        Check.Equal(recovered.CertificateSha256, GamePreparation.Prepare(game).CertificateSha256);

        // An empty existing folder is still existing state: no recovery, even TLS creation.
        MoveState(game, "recovered-state");
        Directory.CreateDirectory(game.Paths.Local);
        Check.Throws<InvalidOperationException>(() => GamePreparation.Prepare(game));
        Check.Equal(0, Directory.GetFileSystemEntries(game.Paths.Local).Length);
        Check.Equal(GameBuild.OriginalSha256, Hash(game.Executable));

        Directory.Delete(game.Paths.Local); // Disposable empty directory only.
        File.WriteAllText(game.Paths.Local, "existing file at the state path");
        Check.Throws<IOException>(() => GamePreparation.Prepare(game));
        Check.Equal("existing file at the state path", File.ReadAllText(game.Paths.Local));
        File.Delete(game.Paths.Local);
        MoveWithinGame(game, Path.Combine(game.Root, "recovered-state"), game.Paths.Local);

        var authorityFile = LocalLicenseAuthority.StatePath(game.Paths);
        var authority = File.ReadAllBytes(authorityFile);
        File.Delete(authorityFile);
        AssertExistingStatePreserved(game, () =>
            Check.Throws<InvalidOperationException>(() => GamePreparation.Prepare(game)));
        File.WriteAllBytes(authorityFile, authority);
        File.WriteAllText(authorityFile, "{corrupt fixture}");
        AssertExistingStatePreserved(game, () =>
            Check.Throws<InvalidDataException>(() => GamePreparation.Prepare(game)));
        Check.Equal("existing game save: preserve exactly", File.ReadAllText(save));
        Check.True(!File.Exists(backup), "Preparation recreated a backup.");
    }

    private static void RefusesMissingState(GameInstallation game, string expectedHash)
    {
        Check.Throws<InvalidDataException>(() => GamePreparation.Prepare(game));
        Check.True(!Directory.Exists(game.Paths.Local), "Refused input created local state.");
        Check.Equal(expectedHash, Hash(game.Executable));
    }

    private static void AssertExistingStatePreserved(GameInstallation game, Action refusal)
    {
        var before = Snapshot(game.Paths.Local);
        var binary = Hash(game.Executable);
        refusal();
        Check.Equal(before, Snapshot(game.Paths.Local));
        Check.Equal(binary, Hash(game.Executable));
    }

    private static string Snapshot(string root) => string.Join('\n',
        Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(path => Path.GetRelativePath(root, path) + ":" + (File.Exists(path) ? Hash(path) : "directory")));

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static void FlipFirstByte(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);
        var value = stream.ReadByte();
        stream.Position = 0;
        stream.WriteByte((byte)(value ^ 1));
    }

    private static void MoveState(GameInstallation game, string name) =>
        MoveWithinGame(game, game.Paths.Local, Path.Combine(game.Root, name));

    private static void MoveWithinGame(GameInstallation game, string source, string destination)
    {
        var root = Path.GetFullPath(game.Root) + Path.DirectorySeparatorChar;
        Check.True(Path.GetFullPath(source).StartsWith(root, StringComparison.OrdinalIgnoreCase)
            && Path.GetFullPath(destination).StartsWith(root, StringComparison.OrdinalIgnoreCase), "Unsafe fixture move.");
        Directory.Move(source, destination);
    }
}
