using Dollhouse.Core;
using Dollhouse.Core.Diagnostics;
using Dollhouse.Core.Patching;
using Dollhouse.Core.Security;
using Dollhouse.Core.Startup;

try
{
    if (args.Length == 0 || args[0] is "--help" or "help")
    {
        Console.WriteLine("""
            Dollhouse tools (.NET 11)
              setup                       Generate/validate persistent local TLS certificate
              probe [--port 7900]         Local HTTPS echo probe, checking trust and pin before HTTP
              probe-winhttp [--port 7900] Windows native WinHTTP probe; checks DER pin after response
            All commands accept --root PATH (source workspace) or --game-root PATH (per-game state).
            No command launches the game or contacts the remote service.
            """);
        return 0;
    }
    var command = args[0];
    var options = new CommandOptions(args[1..]);
    if (options.Has("--root") && options.Has("--game-root"))
        throw new ArgumentException("Choose either --root or --game-root.");
    var paths = options.Get("--game-root") is { } gameRoot
        ? WorkspacePaths.ForGame(gameRoot)
        : WorkspacePaths.Discover(options.Get("--root"));
    object result;
    switch (command)
    {
        case "setup":
            options.Allow("--root", "--game-root");
            result = LocalCertificate.Setup(paths);
            break;
        case "probe":
        case "probe-winhttp":
            options.Allow("--root", "--game-root", "--port");
            var port = int.Parse(options.Get("--port") ?? "7900");
            if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
            if (command == "probe") result = await PinnedHttpsProbe.RunAsync(paths, port);
            else if (OperatingSystem.IsWindows()) result = WinHttpProbe.Run(paths, port);
            else throw new PlatformNotSupportedException("WinHTTP requires Windows.");
            break;
        default:
            throw new ArgumentException($"Unknown command: {command}. Use --help.");
    }
    Console.WriteLine(JsonOutput.Serialize(result));
    return 0;
}
catch (Exception error) when (error is not OutOfMemoryException)
{
    Console.Error.WriteLine(error.Message);
    return 1;
}
