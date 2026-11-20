using Dollhouse.Core.Startup;
using System.Globalization;
using Dollhouse.Server.Echo;
using Dollhouse.Server.Debugging;
using Dollhouse.Core.Patching;

namespace Dollhouse.App;

internal static class TerminalOutput
{
    private static readonly object Gate = new();
    private static int binaryRowLength;
    private static string? binaryOperation;
    private static string? binaryPath;
    private static int lastBinaryPercent;
    private static TimeSpan lastBinaryLog;
    private static bool UseColor => !Console.IsOutputRedirected && !Console.IsErrorRedirected
        && Environment.GetEnvironmentVariable("NO_COLOR") is null
        && Environment.GetEnvironmentVariable("TERM") != "dumb";

    public static void Detail(string message) => Line(message, ConsoleColor.DarkGray);
    public static void BinaryVerification(BinaryProgress update) => Render(() =>
    {
        var starting = binaryOperation != update.Operation || binaryPath != update.Path;
        if (starting)
        {
            binaryOperation = update.Operation;
            binaryPath = update.Path;
            lastBinaryPercent = -25;
            lastBinaryLog = TimeSpan.Zero;
        }
        var percent = update.Percent;
        var text = $"  {update.Operation}: {Path.GetFileName(update.Path)}...{(percent is null ? "" : $" {percent}%")} ({update.Elapsed.TotalSeconds:F0}s)";
        if (update.IsSlow && !update.IsComplete) text += " — taking longer than usual";
        if (Console.IsOutputRedirected)
        {
            if (starting || update.IsComplete || percent >= lastBinaryPercent + 25
                || update.Elapsed - lastBinaryLog >= TimeSpan.FromSeconds(15))
            {
                Console.WriteLine(text);
                lastBinaryPercent = percent ?? lastBinaryPercent;
                lastBinaryLog = update.Elapsed;
            }
        }
        else
        {
            Part("\r" + text.PadRight(binaryRowLength), ConsoleColor.DarkGray);
            binaryRowLength = text.Length;
            if (update.IsComplete) { Console.WriteLine(); binaryRowLength = 0; }
        }
        if (update.IsComplete) binaryOperation = null;
    }, continueBinary: true);

    public static void Preparation(string message)
    {
        if (message.StartsWith("[", StringComparison.Ordinal))
        {
            Line();
            Line(message, ConsoleColor.Cyan);
        }
        else Detail("  " + message);
    }
    public static void Success(string message) => Line(message, ConsoleColor.Green);
    public static void Warning(string message) => Line(message, ConsoleColor.Yellow);
    public static void Error(string message) => Line(message, ConsoleColor.Red, error: true);

    public static void Line(string message = "", ConsoleColor? color = null, bool error = false) => Render(() =>
    {
        if (UseColor && color is not null) Console.ForegroundColor = color.Value;
        (error ? Console.Error : Console.Out).WriteLine(SingleLine(message));
    });

    public static void Request(RequestMetadata metadata, RequestCaptureResult? capture = null)
    {
        var safe = RequestRedactor.Redact(metadata);
        var query = safe.Target.IndexOf('?');
        var path = query < 0 ? safe.Target : safe.Target[..query];
        var queryText = query < 0 ? "" : safe.Target[query..];
        Render(() =>
        {
            if (capture is not null) Console.WriteLine();
            Part($"  {safe.Time.ToLocalTime():HH:mm:ss}  ", ConsoleColor.DarkGray);
            Part($"{SingleLine(safe.Method),-7} ", safe.Method switch
            {
                "GET" or "HEAD" => ConsoleColor.Cyan,
                "POST" => ConsoleColor.Green,
                "PUT" or "PATCH" => ConsoleColor.Yellow,
                "DELETE" => ConsoleColor.Red,
                "WS" => ConsoleColor.Magenta,
                _ => ConsoleColor.Blue
            });
            var tag = safe.Handling switch
            {
                RequestHandling.Api => "[API]",
                RequestHandling.Echo => "[ECHO]",
                _ => "[LOG]"
            };
            Part($"{tag,-6} ", safe.Handling == RequestHandling.Api ? ConsoleColor.Green : ConsoleColor.DarkYellow);
            Part(SingleLine(path), ConsoleColor.White);
            Part(SingleLine(queryText), ConsoleColor.DarkGray);
            if (safe.BodyLength != 0)
                Part($"  {safe.BodyLength.ToString("N0", CultureInfo.CurrentCulture)} B", ConsoleColor.DarkGray);
            Console.WriteLine();
            if (capture is not null)
            {
                Part($"  DEBUG #{capture.RequestId}", ConsoleColor.DarkGray);
                Console.WriteLine();
                Part("  Capture: " + SingleLine(capture.Directory), ConsoleColor.DarkGray);
                Console.WriteLine();
                Part(SingleLine(capture.CurlCommand ?? $"  No curl command: {capture.ExportError}"), ConsoleColor.DarkGray);
                Console.WriteLine();
            }
        });
    }

    private static void Part(string text, ConsoleColor color)
    {
        if (UseColor) Console.ForegroundColor = color;
        Console.Write(text);
    }

    private static void Render(Action write, bool continueBinary = false)
    {
        lock (Gate)
        {
            if (!continueBinary && binaryRowLength != 0)
            {
                Console.WriteLine();
                binaryRowLength = 0;
            }
            var previous = UseColor ? Console.ForegroundColor : (ConsoleColor?)null;
            try { write(); }
            finally { if (previous is not null) Console.ForegroundColor = previous.Value; }
        }
    }

    // Request-controlled text must not inject terminal colors, cursor movement, or log lines.
    private static string SingleLine(string value) => string.Concat(value.Select(character =>
        char.IsControl(character) ? $"\\u{(int)character:x4}" : character.ToString()));
}
