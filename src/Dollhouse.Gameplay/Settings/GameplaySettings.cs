using System.Text.Json;

namespace Dollhouse.Gameplay;

internal sealed record GameplaySettings(bool CumflationEnabled = true, bool IgnoreCumflationRestrictions = false, bool FatigueEnabled = true);

/// <summary>Installation-wide gameplay settings, stored separately from character attributes.</summary>
internal sealed class GameplaySettingsStore
{
    private const int MaximumBytes = 64 * 1024;
    private readonly string path;
    private readonly Action<string> log;
    private JsonElement? previousDocument;
    private bool writable = true;
    public GameplaySettings Current { get; private set; } = new();

    public GameplaySettingsStore(string path, Action<string> log)
    {
        this.path = Path.GetFullPath(path);
        this.log = log;
        try
        {
            // Missing is a default, not a reason to create a file during loading.
            if (!File.Exists(this.path)) { log("settings loaded: default cumflation=True, ignoreCumflationRestrictions=False, fatigue=True; file absent"); return; }
            using var input = new FileStream(this.path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length > MaximumBytes) throw new InvalidDataException("Settings file exceeds size limit.");
            using var document = JsonDocument.Parse(input, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Settings must be an object.");
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
                if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate settings property.");
            if (!root.TryGetProperty("version", out var version) || !version.TryGetInt32(out int number) || number != 1 ||
                !root.TryGetProperty("cumflationEnabled", out var cumflation) || cumflation.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new InvalidDataException("Unsupported settings schema.");
            bool ignoreRestrictions = false; // Existing v1 files acquire the new default without a migration/write.
            if (root.TryGetProperty("ignoreCumflationRestrictions", out var ignore))
            {
                if (ignore.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new InvalidDataException("Invalid restriction setting.");
                ignoreRestrictions = ignore.GetBoolean();
            }
            bool fatigueEnabled = true;
            if (root.TryGetProperty("fatigueEnabled", out var fatigue))
            {
                if (fatigue.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new InvalidDataException("Invalid fatigue setting.");
                fatigueEnabled = fatigue.GetBoolean();
            }
            Current = new(cumflation.GetBoolean(), ignoreRestrictions, fatigueEnabled);
            previousDocument = root.Clone(); // Preserve unrelated settings when this one changes.
            log($"settings loaded: version=1, cumflation={Current.CumflationEnabled}, ignoreCumflationRestrictions={Current.IgnoreCumflationRestrictions}, fatigue={Current.FatigueEnabled}");
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            writable = false;
            log($"settings load failed: {error.GetType().Name}; defaults used, existing file preserved, saving disabled");
        }
    }

    public bool Commit(GameplaySettings next)
    {
        if (!writable) { log("settings save refused: existing file was not loaded safely"); return false; }
        string? temporary = null;
        try
        {
            byte[] bytes;
            using (var buffer = new MemoryStream())
            {
                using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("version", 1);
                    writer.WriteBoolean("cumflationEnabled", next.CumflationEnabled);
                    writer.WriteBoolean("ignoreCumflationRestrictions", next.IgnoreCumflationRestrictions);
                    writer.WriteBoolean("fatigueEnabled", next.FatigueEnabled);
                    if (previousDocument is { } previous)
                        foreach (var property in previous.EnumerateObject())
                            if (property.Name is not ("version" or "cumflationEnabled" or "ignoreCumflationRestrictions" or "fatigueEnabled")) property.WriteTo(writer);
                    writer.WriteEndObject();
                }
                bytes = buffer.ToArray();
            }
            if (bytes.Length > MaximumBytes) throw new InvalidDataException("Settings file exceeds size limit.");
            string directory = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(directory);
            temporary = Path.Combine(directory, ".settings-" + Guid.NewGuid().ToString("N") + ".tmp");
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { output.Write(bytes); output.Flush(flushToDisk: true); }
            File.Move(temporary, path, overwrite: true);
            temporary = null;
            Current = next; // Publish only after the durable replacement succeeds.
            log($"settings saved: version=1, cumflation={next.CumflationEnabled}, ignoreCumflationRestrictions={next.IgnoreCumflationRestrictions}, fatigue={next.FatigueEnabled}");
            return true;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            log($"settings save failed: {error.GetType().Name}; committed value preserved");
            return false;
        }
        finally
        {
            if (temporary != null)
                try { File.Delete(temporary); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                { log($"settings temporary cleanup failed: {error.GetType().Name}"); }
        }
    }
}
