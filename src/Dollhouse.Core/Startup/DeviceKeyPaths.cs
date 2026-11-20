namespace Dollhouse.Core.Startup;

public static class DeviceKeyPaths
{
    public const string KeyPathVariable = "DOLLHOUSE_DEVICE_KEY_FILE";
    public static string KeyPath(GameInstallation installation) =>
        Path.Combine(installation.Paths.Local, "device", "key.json");

}
