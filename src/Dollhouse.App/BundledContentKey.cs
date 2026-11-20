namespace Dollhouse.App;

internal static class BundledContentKey
{
    public static string? Read()
    {
        // Embedded from the tracked resource; never written to logs or runtime files.
        using var resource = typeof(BundledContentKey).Assembly.GetManifestResourceStream("Dollhouse.Content.PakKey");
        if (resource is null) return null;
        using var reader = new StreamReader(resource);
        return reader.ReadToEnd().Trim();
    }
}
