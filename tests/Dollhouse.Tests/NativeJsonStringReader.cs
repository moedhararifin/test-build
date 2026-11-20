using System.Text;

namespace Dollhouse.Tests;

// Static reconstruction of native helper VA 0x1454e4550; not a complete JSON parser.
// It translates \n and \t but copies other escaped characters without Unicode decoding.
internal static class NativeJsonStringReader
{
    public static string Read(string response, string name)
    {
        var field = response.IndexOf('"' + name + '"', StringComparison.Ordinal);
        if (field < 0) return "";
        var colon = response.IndexOf(':', field + name.Length + 2);
        if (colon < 0) return "";
        var quote = response.IndexOf('"', colon);
        if (quote < 0) return "";
        var result = new StringBuilder();
        for (var index = quote + 1; index < response.Length; index++)
        {
            var value = response[index];
            if (value == '"') return result.ToString();
            if (value == '\\' && index + 1 < response.Length)
                value = response[++index] switch { 'n' => '\n', 't' => '\t', var other => other };
            result.Append(value);
        }
        return "";
    }
}
