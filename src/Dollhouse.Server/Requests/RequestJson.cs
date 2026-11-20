using System.Text.Json;

namespace Dollhouse.Server.Requests;

/// <summary>Typed reads for endpoints whose contract requires an object with unique fields.</summary>
internal static class RequestJson
{
    public static JsonDocument ParseObject(ReadOnlyMemory<byte> body)
    {
        var document = JsonDocument.Parse(body);
        try
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException();
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in document.RootElement.EnumerateObject())
                if (!names.Add(field.Name)) throw new JsonException();
            return document;
        }
        catch
        {
            document.Dispose();
            throw;
        }
    }

    public static string String(JsonElement root, string name) => Field(root, name, JsonValueKind.String).GetString()!;

    public static int Int32(JsonElement root, string name) =>
        Field(root, name, JsonValueKind.Number).TryGetInt32(out var value) ? value : throw new JsonException();

    public static long Int64(JsonElement root, string name) =>
        Field(root, name, JsonValueKind.Number).TryGetInt64(out var value) ? value : throw new JsonException();

    public static Dictionary<string, string> StringMap(JsonElement root, string name)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in Field(root, name, JsonValueKind.Object).EnumerateObject())
            if (field.Value.ValueKind != JsonValueKind.String || !result.TryAdd(field.Name, field.Value.GetString()!))
                throw new JsonException();
        return result;
    }

    private static JsonElement Field(JsonElement root, string name, JsonValueKind kind) =>
        root.TryGetProperty(name, out var field) && field.ValueKind == kind ? field : throw new JsonException();
}
