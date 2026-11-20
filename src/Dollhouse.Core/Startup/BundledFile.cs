namespace Dollhouse.Core.Startup;

internal static class BundledFile
{
    public static bool Extract(string resourceName, string path, System.Reflection.Assembly? source = null)
    {
        using var resource = (source ?? typeof(BundledFile).Assembly).GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Bundled resource '{resourceName}' is missing. Build with build.cmd.");
        if (File.Exists(path))
        {
            using var installed = File.OpenRead(path);
            if (EqualStreams(resource, installed)) return false;
            resource.Position = 0;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                resource.CopyTo(stream, 64 * 1024);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return true;
    }

    private static bool EqualStreams(Stream expected, Stream actual)
    {
        if (expected.Length != actual.Length) return false;
        byte[] left = new byte[64 * 1024], right = new byte[64 * 1024];
        int count;
        while ((count = expected.Read(left)) != 0)
        {
            actual.ReadExactly(right.AsSpan(0, count));
            if (!left.AsSpan(0, count).SequenceEqual(right.AsSpan(0, count))) return false;
        }
        return true;
    }
}
