using System.Text;

namespace Dollhouse.Gameplay;

internal static class CameraPresetFile
{
    // Read a bounded snapshot. Decoding permits standard BOMs; the game's existing
    // JSON parser, not a second schema implementation, interprets the contents.
    internal static string Read(string? path, Action<string> log)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
                throw new InvalidDataException("The runtime camera path must be absolute.");
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length > CameraBindings.MaximumBytes)
                throw new InvalidDataException("Camera config exceeds the 32 MiB limit.");
            var bytes = new byte[checked((int)input.Length)];
            input.ReadExactly(bytes);
            if (input.ReadByte() != -1) throw new IOException("Camera config changed while being read.");
            using var reader = new StreamReader(new MemoryStream(bytes, writable: false),
                new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException
            or InvalidDataException or DecoderFallbackException)
        {
            log($"presets read failed: path={path ?? "<unset>"}, {error.GetType().Name}: {error.Message}; parsing an empty list");
            return "[]";
        }
    }
}
