using System.Net.Sockets;

namespace Dollhouse.App;

internal static class StartupErrors
{
    public static IEnumerable<string> Describe(string operation, string? path, Exception error, bool debug)
    {
        yield return $"Could not {operation}: {error.Message}";
        if (path is not null) yield return $"Location: {path}";
        var cause = error.GetBaseException();
        yield return $"Error: {cause.GetType().Name} (0x{cause.HResult:X8})";
        if (cause is SocketException socket)
        {
            yield return $"Socket error: {socket.SocketErrorCode}";
            if (socket.SocketErrorCode == SocketError.AccessDenied)
                yield return "Windows denied use of the local port. Check reserved ports and security software; the port may not be occupied.";
        }
        else if (cause is UnauthorizedAccessException)
            yield return "Check that this folder and its files are writable, not read-only or blocked by security software. Dollhouse does not change folder permissions.";
        else if (cause is IOException && (cause.HResult & 0xffff) is 32 or 33)
            yield return "A required file is locked. Close the game or other program using it, then retry.";
        else if (cause is IOException && (cause.HResult & 0xffff) is 39 or 112)
            yield return "The drive is full. Free space for the original backup and generated files, then retry.";
        if (debug)
            foreach (var line in (error.StackTrace ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries))
                yield return line.Trim();
    }
}
