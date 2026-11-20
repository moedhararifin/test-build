using System.Reflection.PortableExecutable;
using System.Text.Json;
using Dollhouse.Core.Patching;
using Dollhouse.Core.Patching.Native;
using Dollhouse.Gameplay;
using SettingsContract = Dollhouse.Gameplay.SettingsBindings;

namespace Dollhouse.Tests;

internal static class GameplaySettingsTests
{
    internal static void Verify()
    {
        using var workspace = new TemporaryWorkspace();
        string path = Path.Combine(workspace.Paths.Root, ".dollhouse", "settings.json");
        var logs = new List<string>();
        var store = new GameplaySettingsStore(path, logs.Add);
        Check.True(store.Current.CumflationEnabled && !File.Exists(path), "Loading defaults must not create settings.");
        Check.True(!store.Current.IgnoreCumflationRestrictions, "Restriction bypass must default OFF.");
        Check.True(store.Current.FatigueEnabled, "Fatigue must default ON.");
        Check.True(store.Commit(new(false, true, false)), "Could not disable fatigue.");
        var fatigueReload = new GameplaySettingsStore(path, logs.Add).Current;
        Check.True(!fatigueReload.FatigueEnabled && !fatigueReload.CumflationEnabled && fatigueReload.IgnoreCumflationRestrictions,
            "Fatigue save lost another option or did not survive reopening.");
        Check.True(store.Commit(new(true, true)), "Could not enable restriction setting.");
        Check.True(new GameplaySettingsStore(path, logs.Add).Current.IgnoreCumflationRestrictions, "Restriction bypass was not durable.");
        Check.True(store.Commit(new(false)), "Apply failed.");
        Check.True(!new GameplaySettingsStore(path, logs.Add).Current.CumflationEnabled, "OFF did not survive reopening the store.");
        File.WriteAllText(path, "{\"version\":1,\"cumflationEnabled\":false,\"futureSetting\":{\"value\":2}}");
        store = new GameplaySettingsStore(path, logs.Add);
        Check.True(!store.Current.IgnoreCumflationRestrictions, "Old v1 settings must default the new key OFF.");
        Check.True(store.Current.FatigueEnabled, "Existing v1 files must default fatigue ON.");
        Check.True(store.Commit(new(true)), "Could not save valid settings.");
        using (var json = JsonDocument.Parse(File.ReadAllBytes(path)))
            Check.Equal(2, json.RootElement.GetProperty("futureSetting").GetProperty("value").GetInt32());

        foreach (string invalid in new[]
        {
            "", "[]", "{\"version\":2,\"cumflationEnabled\":false}",
            "{\"version\":1,\"cumflationEnabled\":\"false\"}",
            "{\"version\":1,\"cumflationEnabled\":true,\"ignoreCumflationRestrictions\":\"true\"}",
            "{\"version\":1,\"cumflationEnabled\":true,\"fatigueEnabled\":\"true\"}",
            "{\"version\":1,\"cumflationEnabled\":true,\"fatigueEnabled\":null}",
            "{\"version\":1,\"cumflationEnabled\":true,\"fatigueEnabled\":true,\"fatigueEnabled\":false}",
            "{\"version\":1,\"cumflationEnabled\":false,\"cumflationEnabled\":true}"
        })
        {
            File.WriteAllText(path, invalid);
            var bad = new GameplaySettingsStore(path, logs.Add);
            Check.True(!bad.Commit(new(false)), "Unreadable/unsupported existing settings were overwritten.");
            Check.Equal(invalid, File.ReadAllText(path));
        }
        File.Delete(path);
        store = new GameplaySettingsStore(path, logs.Add);
        Directory.CreateDirectory(path); // Same-directory rename must fail without replacing this directory.
        Check.True(!store.Commit(new(false, true)) && store.Current.CumflationEnabled && !store.Current.IgnoreCumflationRestrictions,
            "Failed save changed committed state or enabled the restriction bypass.");
        Directory.Delete(path);
        Check.True(store.Commit(new(false)), "Failed settings save was not retryable.");
        Check.True(logs.Any(x => x.Contains("save failed")) && logs.Any(x => x.Contains("save refused")), "Missing failure logs.");
        SettingsMenuNativeFixture.Verify(workspace.Paths.Root);
        CumflationSettingTests.Verify();
        BulgeEligibilityFixture.Verify();
        VerifyRecipe();
    }

    private static void VerifyRecipe()
    {
        if (GameBinaryFixture.Original is not { } path) return;
        byte[] original = File.ReadAllBytes(path);
        var plan = new NativePatchPlan(GameBuild.ExpectedSize);
        SettingsMenuPatchRecipe.AddTo(plan);
        var layout = plan.BuildMemory(original, 0x60000000, _ => 0x2000);
        Check.True(layout.Data.Length >= (SettingsContract.OriginalRowCount + SettingsContract.AddedRowCount) * SettingsContract.RowSize,
            "Expanded settings descriptors have no writable runtime storage.");
        using var image = new PeImage(original);
        foreach (var edit in layout.Edits)
            Check.True(original.AsSpan(image.FileOffset(edit.Rva), edit.Expected.Length).SequenceEqual(edit.Expected), "Settings edit lost its full preimage.");
    }
}
