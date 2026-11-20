using System.Security.Cryptography;
using System.Text.Json;
using Dollhouse.Core.Startup;
using Dollhouse.Gameplay;

namespace Dollhouse.Tests;

internal static class CameraTests
{
    internal static void Verify()
    {
        using var temporary = new TemporaryWorkspace();
        var installation = new GameInstallation(Path.Combine(temporary.Paths.Root, "game.exe"), temporary.Paths.Root);
        string path = CameraConfiguration.PathFor(installation);
        Check.True(!Directory.Exists(installation.SavedDirectory), "Fresh fixture already has Saved.");
        Check.True(CameraConfiguration.Prepare(installation), "Default presets were not created.");
        Check.True(!Directory.Exists(installation.SavedDirectory), "Camera deployment created Saved.");
        Check.Equal("802fdb2d56d5173b69d7c6bbc1791fd6f3efbd80affcfa81372b744fe420aa17",
            Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))));
        using (var json = JsonDocument.Parse(File.ReadAllBytes(path))) Check.Equal(16519, json.RootElement.GetArrayLength());
        Check.True(!CameraConfiguration.Prepare(installation), "Repeat preparation rewrote presets.");
        string custom = "[{\"AnimID\":17,\"PosID\":2,\"len\":-10}]";
        File.WriteAllText(path, custom);
        Check.True(!CameraConfiguration.Prepare(installation), "Custom presets were overwritten.");
        Check.Equal(custom, File.ReadAllText(path));
        Directory.CreateDirectory(installation.SavedDirectory);
        File.WriteAllText(Path.Combine(installation.SavedDirectory, "settings.txt"), "synthetic");
        Directory.Delete(installation.SavedDirectory, recursive: true); // Isolated fixture only.
        Check.Equal(custom, File.ReadAllText(path));
        File.WriteAllText(path, "{ malformed customization");
        CameraConfiguration.Prepare(installation);
        Check.Equal("{ malformed customization", File.ReadAllText(path));
        var logs = new List<string>();
        Check.Equal("[]", CameraPresetFile.Read(null, logs.Add));
        Check.Equal("[]", CameraPresetFile.Read("relative.config", logs.Add));
        Check.Equal("[]", CameraPresetFile.Read(path + ".missing", logs.Add));
        using (var file = new FileStream(path, FileMode.Create)) file.SetLength(CameraBindings.MaximumBytes + 1L);
        Check.Equal("[]", CameraPresetFile.Read(path, logs.Add));
        File.WriteAllText(path, "[]", new System.Text.UTF8Encoding(true));
        Check.Equal("[]", CameraPresetFile.Read(path, logs.Add));
        File.WriteAllBytes(path, [0xff, 0xff, 0xff]);
        Check.Equal("[]", CameraPresetFile.Read(path, logs.Add));
        Check.True(logs.Any(x => x.Contains("read failed")), "File failures were not diagnosed.");
        File.Delete(path);
        Check.True(CameraConfiguration.Prepare(installation), "Missing presets were not re-created.");
    }
}
