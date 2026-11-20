using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Dollhouse.Core;
using Dollhouse.Server.Debugging;

namespace Dollhouse.Server.Echo;

public interface IRequestJournal
{
    void Write(RequestMetadata metadata);
    void Write(RequestMetadata metadata, RequestCaptureResult? capture) => Write(metadata);
}

public static class RequestRedactor
{
    private static readonly string[] SecretWords =
        ["authorization", "cookie", "password", "token", "secret", "lease", "key", "session"];

    private static bool Sensitive(string name) => SecretWords.Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase));

    public static RequestMetadata Redact(RequestMetadata metadata)
    {
        var separator = metadata.Target.IndexOf('?');
        var target = metadata.Target;
        if (separator >= 0)
        {
            var query = QueryHelpers.ParseQuery(target[(separator + 1)..]);
            target = QueryHelpers.AddQueryString(target[..separator], query.SelectMany(pair =>
                pair.Value.Select(value => new KeyValuePair<string, string?>(pair.Key,
                    Sensitive(pair.Key) ? "[redacted]" : value))));
        }
        return metadata with
        {
            Target = target,
            Headers = metadata.Headers.Select(pair => new[] { pair[0], Sensitive(pair[0]) ? "[redacted]" : pair[1] }).ToArray()
        };
    }
}

public sealed class ConsoleRequestJournal : IRequestJournal
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Options = new(JsonOutput.Options) { WriteIndented = false };

    public void Write(RequestMetadata metadata)
    {
        var line = JsonSerializer.Serialize(RequestRedactor.Redact(metadata), Options);
        lock (Gate) Console.WriteLine(line);
    }
}
