using System.Runtime.InteropServices;
using Dollhouse.Gameplay;

namespace Dollhouse.Tests;

internal sealed unsafe class SettingsMenuNativeFixture : IDisposable
{
    private static SettingsMenuNativeFixture Current = null!;
    private readonly List<nint> buffers = [];
    private readonly nint original, expanded;
    private readonly List<string> logs = [];
    private readonly NativeGame game;
    private int builds, foreignReads, foreignSets, foreignDefaults, foreignEffective, applies, refreshes;
    private int pending = -1, baseline;
    private readonly byte[] before;
    private nint Allocate(int size) { nint value = (nint)NativeMemory.AllocZeroed((nuint)size); buffers.Add(value); return value; }
    private nint Text(string value)
    {
        nint text = Allocate((value.Length + 1) * 2);
        value.AsSpan().CopyTo(new Span<char>((void*)text, value.Length));
        return text;
    }
    private SettingsMenuNativeFixture()
    {
        Current = this;
        original = Allocate(14 * 24); expanded = Allocate((SettingsBindings.OriginalRowCount + SettingsBindings.AddedRowCount) * SettingsBindings.RowSize);
        for (int i = 0; i < 14; i++)
        {
            *(nint*)(original + i * 24) = Text("Existing" + i);
            *(nint*)(original + i * 24 + 8) = Text("Existing label " + i);
        }
        before = new ReadOnlySpan<byte>((void*)original, 14 * 24).ToArray();
        game = new(0, rva => rva switch
        {
            SettingsBindings.NativeRows => original,
            SettingsBindings.Build => (nint)(delegate* unmanaged<nint, void>)&Build,
            SettingsBindings.ReadRow => (nint)(delegate* unmanaged<nint, int>)&Read,
            SettingsBindings.Default => (nint)(delegate* unmanaged<GameString*, char*, GameString*>)&Default,
            SettingsBindings.Effective => (nint)(delegate* unmanaged<GameString*, char*, GameString*>)&Effective,
            SettingsBindings.Set => (nint)(delegate* unmanaged<char*, GameString*, void>)&Set,
            SettingsBindings.Apply => (nint)(delegate* unmanaged<nint, void>)&Apply,
            SettingsBindings.Refresh => (nint)(delegate* unmanaged<nint, void>)&Refresh,
            SupportedBuild.CopyString => (nint)(delegate* unmanaged<GameString*, GameString*, nint>)&Copy,
            SupportedBuild.GameplayLog => (nint)(delegate* unmanaged<char*, void>)&Log,
            _ => throw new InvalidOperationException("Unexpected settings native API.")
        });
    }
    public void Dispose() { foreach (nint p in buffers) NativeMemory.Free((void*)p); Current = null!; }
    internal static void Verify(string root)
    {
        string? previous = Environment.GetEnvironmentVariable(SettingsBindings.PathVariable);
        string path = Path.Combine(root, "native-menu-settings.json");
        Environment.SetEnvironmentVariable(SettingsBindings.PathVariable, path);
        try
        {
            using var f = new SettingsMenuNativeFixture();
            f.game.BuildSettingsMenu(1, f.expanded); f.game.BuildSettingsMenu(2, f.expanded);
            Check.Equal(2, f.builds);
            Check.True(new ReadOnlySpan<byte>((void*)f.original, 14 * 24).SequenceEqual(f.before), "Original descriptors changed.");
            Check.True(new ReadOnlySpan<byte>((void*)f.expanded, 14 * 24).SequenceEqual(f.before), "Existing rows were not preserved.");
            nint custom = f.expanded + 14 * 24;
            Check.Equal(SettingsBindings.CumflationKey, new string(*(char**)custom));
            Check.Equal(SettingsBindings.CumflationLabel, new string(*(char**)(custom + 8)));
            Check.Equal((byte)0, *(byte*)(custom + 16)); Check.Equal((byte)0, *(byte*)(custom + 17));
            Check.Equal(1, f.game.ReadSettingsRow(custom));
            nint restrictions = f.expanded + 15 * 24;
            Check.Equal(SettingsBindings.IgnoreRestrictionsKey, new string(*(char**)restrictions));
            Check.Equal(SettingsBindings.IgnoreRestrictionsLabel, new string(*(char**)(restrictions + 8)));
            Check.Equal((byte)0, *(byte*)(restrictions + 16)); Check.Equal((byte)0, *(byte*)(restrictions + 17));
            Check.Equal(0, f.game.ReadSettingsRow(restrictions));
            nint fatigue = f.expanded + 16 * 24;
            Check.Equal(SettingsBindings.FatigueKey, new string(*(char**)fatigue));
            Check.Equal(SettingsBindings.FatigueLabel, new string(*(char**)(fatigue + 8)));
            Check.Equal((byte)0, *(byte*)(fatigue + 16)); Check.Equal((byte)0, *(byte*)(fatigue + 17));
            Check.Equal(1, f.game.ReadSettingsRow(fatigue));
            Check.True(f.logs.Any(x => x.StartsWith("[Dollhouse settings] settings loaded:") && x.Contains("fatigue=True")),
                "Settings diagnostics must use the shared settings prefix and include the fatigue default.");
            fixed (char* key = SettingsBindings.FatigueKey)
            fixed (char* zero = "0")
            {
                GameString value = new() { Data = zero, Length = 2, Capacity = 2 }, output = default;
                f.game.SetSetting(key, &value);
                Check.Equal(0, f.game.ReadSettingsRow(fatigue));
                f.game.DefaultSetting(&output, key); Check.Equal("1", new string(output.Data));
                Check.Equal(0, f.game.ReadSettingsRow(fatigue)); // Reset remains staged.
                f.game.EffectiveSetting(&output, key); Check.Equal("0", new string(output.Data));
                Check.True(!new GameplaySettingsStore(path, f.logs.Add).Current.FatigueEnabled, "Fatigue OFF did not persist.");
                Check.Equal(1, f.game.ReadSettingsRow(custom)); Check.Equal(0, f.game.ReadSettingsRow(restrictions));
            }
            Check.Equal(77, f.game.ReadSettingsRow(f.expanded)); Check.Equal(1, f.foreignReads);
            fixed (char* key = SettingsBindings.CumflationKey)
            fixed (char* other = "Existing")
            fixed (char* zero = "0")
            {
                GameString value = new() { Data = zero, Length = 2, Capacity = 2 }, output = default;
                f.game.SetSetting(key, &value);
                Check.Equal(0, f.game.ReadSettingsRow(custom));
                f.game.DefaultSetting(&output, key); Check.Equal("1", new string(output.Data));
                Check.Equal(0, f.game.ReadSettingsRow(custom)); // Reset only supplies a default; it does not save.
                f.game.EffectiveSetting(&output, key); Check.Equal("0", new string(output.Data));
                f.game.SetSetting(other, &value); f.game.DefaultSetting(&output, other); f.game.EffectiveSetting(&output, other);
                Check.Equal(1, f.foreignSets); Check.Equal(1, f.foreignDefaults); Check.Equal(1, f.foreignEffective);
            }
            f.game.ApplySettings(3); Check.Equal(1, f.applies); Check.Equal(1, f.refreshes);
            Check.True(!new GameplaySettingsStore(path, f.logs.Add).Current.CumflationEnabled, "Native Apply value did not persist.");
            f.pending = 1; // Native OnSelect stages +0x30 without touching disk/current state.
            Check.Equal(0, f.game.ReadSettingsRow(custom));
            File.Delete(path); Directory.CreateDirectory(path); // Cause the actual atomic save to fail.
            f.game.ApplySettings(3);
            Check.Equal(0, f.baseline); // Refresh must undo native Apply's optimistic committed index.
            Check.Equal(0, f.game.ReadSettingsRow(custom));
            Directory.Delete(path);
            f.game.ApplySettings(3);
            Check.Equal(1, f.baseline);
            Check.True(new GameplaySettingsStore(path, f.logs.Add).Current.CumflationEnabled, "Native Apply retry did not persist.");
            fixed (char* key = SettingsBindings.IgnoreRestrictionsKey)
            fixed (char* one = "1")
            fixed (char* zero = "0")
            {
                GameString value = new() { Data = one, Length = 2, Capacity = 2 }, output = default;
                f.game.SetSetting(key, &value);
                Check.Equal(1, f.game.ReadSettingsRow(restrictions));
                Check.True(new GameplaySettingsStore(path, f.logs.Add).Current.IgnoreCumflationRestrictions, "New menu setting was not saved.");
                Check.Equal(1, f.game.ReadSettingsRow(custom)); // Independent of the existing Cumflation toggle.
                f.game.DefaultSetting(&output, key); Check.Equal("0", new string(output.Data));
                Check.Equal(1, f.game.ReadSettingsRow(restrictions)); // Reset stages the default only.
                f.game.EffectiveSetting(&output, key); Check.Equal("1", new string(output.Data));
                value.Data = zero; f.game.SetSetting(key, &value);
                Check.Equal(0, f.game.ReadSettingsRow(restrictions));
            }
            Check.True(!f.logs.Any(x => x.Contains("menu committed index") || x.Contains("controls refreshed")), "Routine native menu operations must be silent.");
        }
        finally { Environment.SetEnvironmentVariable(SettingsBindings.PathVariable, previous); }
    }
    [UnmanagedCallersOnly] private static void Build(nint menu) => Current.builds++;
    [UnmanagedCallersOnly] private static int Read(nint descriptor) { Current.foreignReads++; return 77; }
    [UnmanagedCallersOnly] private static GameString* Default(GameString* output, char* key) { Current.foreignDefaults++; *output = default; return output; }
    [UnmanagedCallersOnly] private static GameString* Effective(GameString* output, char* key) { Current.foreignEffective++; *output = default; return output; }
    [UnmanagedCallersOnly] private static void Set(char* key, GameString* value) => Current.foreignSets++;
    [UnmanagedCallersOnly] private static void Apply(nint hook)
    {
        var f = Current; f.applies++;
        if (f.pending < 0) return;
        fixed (char* key = SettingsBindings.CumflationKey)
        fixed (char* chars = f.pending == 1 ? "1" : "0")
        {
            GameString value = new() { Data = chars, Length = 2, Capacity = 2 };
            f.game.SetSetting(key, &value);
        }
        f.baseline = f.pending; // Native Apply writes +0x2c even when Set failed.
    }
    [UnmanagedCallersOnly] private static void Refresh(nint hook)
    {
        var f = Current; f.refreshes++;
        f.baseline = f.game.ReadSettingsRow(f.expanded + 14 * 24);
    }
    [UnmanagedCallersOnly] private static nint Copy(GameString* target, GameString* source)
    {
        char* copy = (char*)Current.Allocate(source->Length * 2);
        new ReadOnlySpan<char>(source->Data, source->Length).CopyTo(new Span<char>(copy, source->Length));
        *target = new() { Data = copy, Length = source->Length, Capacity = source->Length };
        return (nint)target;
    }
    [UnmanagedCallersOnly] private static void Log(char* text) => Current.logs.Add(new string(text));
}
