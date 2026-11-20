using Dollhouse.App;
using Dollhouse.Server.Echo;

namespace Dollhouse.Tests;

internal static class TerminalOutputTests
{
    public static async Task VerifyAsync()
    {
        using var output = new StringWriter();
        var previous = Console.Out;
        try
        {
            Console.SetOut(output);
            // Both routes must occupy the same columns; normal requests have no
            // empty spacer rows, including when emitted concurrently.
            await Task.WhenAll(Enumerable.Range(0, 16).Select(index => Task.Run(() =>
                TerminalOutput.Request(new(DateTimeOffset.UtcNow, index % 2 == 0 ? "GET" : "POST",
                    $"/api/v1/test/{index}", [], index % 2 == 0 ? 0 : 233,
                    index % 2 == 0 ? RequestHandling.Api : RequestHandling.Echo)))));
        }
        finally { Console.SetOut(previous); }
        var lines = output.ToString().Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n').Split('\n');
        Check.Equal(16, lines.Length);
        Check.True(lines.All(line => !string.IsNullOrWhiteSpace(line)), "Normal request logs contain blank rows.");
        Check.Equal(1, lines.Select(line => line.IndexOf("/api/v1/", StringComparison.Ordinal)).Distinct().Count());
        foreach (var line in lines)
        {
            var api = line.Contains("[API]", StringComparison.Ordinal);
            Check.True(line.IndexOf(api ? "[API]" : "[ECHO]", StringComparison.Ordinal)
                < line.IndexOf("/api/v1/", StringComparison.Ordinal), "Handler tag must precede the endpoint.");
            Check.True(!line.Contains("0 B", StringComparison.Ordinal), "Empty payload size adds noise.");
            if (!api) Check.True(line.EndsWith("233 B", StringComparison.Ordinal), "Nonempty payload size is missing.");
        }
        Check.Equal(16, lines.Select(line => line.Split("/api/v1/test/", StringSplitOptions.None)[1].Split(' ')[0]).Distinct().Count());

        using var escaped = new StringWriter();
        try
        {
            Console.SetOut(escaped);
            TerminalOutput.Request(new(DateTimeOffset.UtcNow, "GET", "/api/v1/test\n\u001b[31m?session=fixture-secret", [], 0, RequestHandling.Api));
        }
        finally { Console.SetOut(previous); }
        Check.True(!escaped.ToString().Contains('\u001b') && !escaped.ToString().Contains("fixture-secret", StringComparison.Ordinal),
            "Compact output lost terminal escaping or credential redaction.");
        Check.Equal(1, escaped.ToString().Count(character => character == '\n'));
    }
}
