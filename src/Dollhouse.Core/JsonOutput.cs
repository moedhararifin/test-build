using System.Text.Json;

namespace Dollhouse.Core;

public static class JsonOutput
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
}
