using System.Text;
using Dollhouse.Core.Patching;
using Dollhouse.Server.Echo;

namespace Dollhouse.Server.Debugging;

public static class CurlReplay
{
    public static Uri OriginalServer { get; } = new(GameBuild.OldUrl);
    private static readonly string[] HopHeaders = ["Host", "Content-Length", "Transfer-Encoding", "Connection",
        "Keep-Alive", "Proxy-Connection", "Proxy-Authenticate", "Proxy-Authorization", "TE", "Trailer", "Upgrade"];

    public static string[] Headers(EchoRequest request)
    {
        var excluded = new HashSet<string>(HopHeaders, StringComparer.OrdinalIgnoreCase);
        foreach (var connection in request.Headers.Where(pair => pair[0].Equals("Connection", StringComparison.OrdinalIgnoreCase)))
            foreach (var name in connection[1].Split(',')) excluded.Add(name.Trim());
        var websocket = request.Headers.Any(pair => pair[0].Equals("Upgrade", StringComparison.OrdinalIgnoreCase)
            && pair[1].Equals("websocket", StringComparison.OrdinalIgnoreCase));
        var result = new List<string>();
        foreach (var pair in request.Headers)
        {
            if (pair[0].Contains('\r') || pair[0].Contains('\n') || pair[1].Contains('\r') || pair[1].Contains('\n'))
                throw new ArgumentException("Header values cannot contain line breaks.");
            var keepUpgrade = websocket && (pair[0].Equals("Connection", StringComparison.OrdinalIgnoreCase)
                || pair[0].Equals("Upgrade", StringComparison.OrdinalIgnoreCase));
            if (!excluded.Contains(pair[0]) || keepUpgrade)
                result.Add(pair[0] + (pair[1].Length == 0 ? ";" : ": " + pair[1]));
        }
        foreach (var name in new[] { "Accept", "Content-Type", "User-Agent", "Expect" })
            if (!request.Headers.Any(pair => pair[0].Equals(name, StringComparison.OrdinalIgnoreCase))) result.Add(name + ":");
        return result.ToArray();
    }

    public static string CreateCommand(EchoRequest request, ReadOnlySpan<byte> body, string captureDirectory, Uri? origin = null)
    {
        origin ??= OriginalServer;
        if (origin.Scheme != "https" || origin.UserInfo.Length != 0 || origin.AbsolutePath != "/"
            || origin.Query.Length != 0 || origin.Fragment.Length != 0)
            throw new ArgumentException("Replay origin must be an HTTPS authority.", nameof(origin));
        if (!request.Target.StartsWith('/') || request.Target.Any(c => c is '\r' or '\n' or '\0' or '"'))
            throw new ArgumentException("Curl export requires an origin-form request target without control characters.");
        var command = new StringBuilder("curl.exe --disable --insecure --silent --show-error --include --http1.1 --globoff --path-as-is --noproxy '*' --proto '=https' --connect-timeout 10 --max-time 60");
        command.Append(" --request ").Append(Quote(request.Method));
        command.Append(" --url ").Append(Quote(origin.GetLeftPart(UriPartial.Authority) + request.Target));
        var headers = Headers(request);
        if (headers.Any(header => !SafeInline(Encoding.UTF8.GetBytes(header))))
            command.Append(" --header ").Append(Quote("@" + Path.GetFullPath(Path.Combine(captureDirectory, "headers.txt"))));
        else
            foreach (var header in headers) command.Append(" --header ").Append(Quote(header));
        if (request.Method.Equals("HEAD", StringComparison.OrdinalIgnoreCase)) command.Append(" --head");
        else if (body.Length > 0 || request.Headers.Any(pair => pair[0].Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
            || pair[0].Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)))
        {
            command.Append(" --data-binary ");
            command.Append(Quote(SafeInline(body) && (body.Length == 0 || body[0] != '@')
                ? Encoding.ASCII.GetString(body) : "@" + Path.GetFullPath(Path.Combine(captureDirectory, "body.bin"))));
        }
        return command.ToString();
    }

    // Plain PowerShell quoting, with file fallback for quotes/binary data that
    // legacy Windows native-argument marshalling could otherwise change.
    private static bool SafeInline(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > 512) return false;
        foreach (var value in bytes)
            if (value < 32 || value > 126 || value == '"') return false;
        return true;
    }

    private static string Quote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
}
