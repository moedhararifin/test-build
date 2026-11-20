using Dollhouse.Core.Patching;

namespace Dollhouse.Core.Startup;

public static class CameraConfiguration
{
    public static string PathFor(GameInstallation installation) =>
        Path.Combine(installation.Paths.Local, "config", CameraBindings.FileName);

    // Unlike Engine.ini, this is a user-editable preset collection. Never repair
    // differing bytes over a customization, including a malformed customization.
    public static bool Prepare(GameInstallation installation)
    {
        string path = PathFor(installation);
        if (File.Exists(path)) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var resource = typeof(CameraConfiguration).Assembly.GetManifestResourceStream("Dollhouse.Camera.CamConf")
                ?? throw new InvalidOperationException("Bundled camera presets are missing."))
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                resource.CopyTo(output, 64 * 1024);
                output.Flush(flushToDisk: true);
            }
            try { File.Move(temporary, path, overwrite: false); }
            catch (IOException) when (File.Exists(path)) { return false; } // Another creator's file wins.
            return true;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
