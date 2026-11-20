using System.Diagnostics;

namespace Dollhouse.Core.Startup;

public sealed record GameInstallation(string Executable, string Root)
{
    public const string ProjectName = "Paralogue";
    public const string ShippingName = "Paralogue-Win64-Shipping.exe";
    public string ProjectDirectory => Path.Combine(Path.GetFullPath(Root), ProjectName);
    public string SavedDirectory => Path.Combine(ProjectDirectory, "Saved");
    public WorkspacePaths Paths => WorkspacePaths.ForGame(Root);

    public void EnsureNotRunning()
    {
        var names = new[] { Path.GetFileNameWithoutExtension(Executable), Path.GetFileNameWithoutExtension(ShippingName) };
        foreach (var name in names.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var processes = Process.GetProcessesByName(name);
            foreach (var process in processes) process.Dispose();
            if (processes.Length != 0)
                throw new InvalidOperationException("Close the game before preparing or cleaning this installation.");
        }
    }

    public static GameInstallation Locate(string? target, string applicationDirectory, string workingDirectory)
    {
        if (target is not null)
        {
            var file = Path.GetFullPath(target);
            if (!File.Exists(file)) throw new FileNotFoundException("Game executable not found.", file);
            if (!Path.GetExtension(file).Equals(".exe", StringComparison.OrdinalIgnoreCase)
                || Path.GetFileNameWithoutExtension(file).EndsWith(".backup", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Select the game EXE, not its backup.");
            return FromExecutable(file);
        }
        // Prefer the program's location, so double-click behavior does not depend on Explorer's CWD.
        foreach (var directory in new[] { applicationDirectory, workingDirectory }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var candidates = new[]
            {
                Path.Combine(directory, ProjectName, "Binaries", "Win64", ShippingName),
                Path.Combine(directory, "Binaries", "Win64", ShippingName),
                Path.Combine(directory, ShippingName)
            }.Where(File.Exists).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (candidates.Length == 1) return FromExecutable(candidates[0]);
            if (candidates.Length > 1) throw new InvalidOperationException("More than one game was found. Run Dollhouse.exe with the path to the game EXE.");
        }
        throw new FileNotFoundException("Game not found. Put Dollhouse.exe in the game folder, or run: Dollhouse.exe \"C:\\path\\to\\game.exe\"");
    }

    private static GameInstallation FromExecutable(string executable)
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(executable)!);
        var root = directory.Name.Equals("Win64", StringComparison.OrdinalIgnoreCase)
            && directory.Parent?.Name.Equals("Binaries", StringComparison.OrdinalIgnoreCase) == true
            && directory.Parent.Parent?.Name.Equals(ProjectName, StringComparison.OrdinalIgnoreCase) == true
            ? directory.Parent.Parent.Parent!.FullName : directory.FullName;
        return new(executable, root);
    }
}
