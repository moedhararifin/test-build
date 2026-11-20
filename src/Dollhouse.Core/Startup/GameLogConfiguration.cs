namespace Dollhouse.Core.Startup;

public static class GameLogConfiguration
{
    public static string PathFor(GameInstallation installation) =>
        Path.Combine(installation.Paths.Local, "config", "Engine.ini");

    public static bool Prepare(GameInstallation installation) =>
        BundledFile.Extract("Dollhouse.Game.Engine.ini", PathFor(installation));
}
