using System.Reflection;

namespace Dollhouse.Core.Startup;

public static class RuntimeCompatibility
{
    public const string LibraryName = "dhruntime.dll";
    public static string LibraryPath(GameInstallation installation) => Path.Combine(installation.Paths.Local, "runtime", LibraryName);
    internal static string PreviousLibraryPath(GameInstallation installation) => Path.Combine(Path.GetDirectoryName(installation.Executable)!, LibraryName);
    public static bool IsBundled => Assembly.Load("Dollhouse").GetManifestResourceNames().Contains("Dollhouse.Runtime");

    public static bool Prepare(GameInstallation installation)
    {
        installation.EnsureNotRunning();
        // The application embeds the runtime, avoiding a Core -> runtime -> Core
        // resource/build cycle. The DLL is the only extracted support binary.
        string path = LibraryPath(installation);
        bool changed = BundledFile.Extract("Dollhouse.Runtime", path, Assembly.Load("Dollhouse"));
        // This exact filename was managed by earlier releases. Retire it only
        // after the new runtime is installed; extraction failure preserves it.
        string previous = PreviousLibraryPath(installation);
        if (!previous.Equals(path, StringComparison.OrdinalIgnoreCase) && File.Exists(previous))
        {
            File.Delete(previous);
            changed = true;
        }
        return changed;
    }
}
