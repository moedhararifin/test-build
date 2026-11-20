using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Dollhouse.App;
using Dollhouse.Core.Startup;
using Dollhouse.Server;
using Dollhouse.Server.Debugging;
using Dollhouse.Server.Endpoints;
using Dollhouse.Server.Accounts;
using Dollhouse.Server.Licensing;

var pauseOnError = ExplorerConsole.IsStandalone;
var startupOperation = "read startup options";
string? startupPath = null;
var debugStartup = false;
void Operation(string name, string? path = null) { startupOperation = name; startupPath = path; }
try
{
    if (args is ["--help"] or ["-h"])
    {
        Console.WriteLine("""
            Place Dollhouse.exe in the game's root folder and double-click it.
            Or run: Dollhouse.exe "C:\path\to\game.exe"
                    Dollhouse.exe --target "C:\path\to\game.exe"
            Add --debug to capture full requests and print copyable curl commands.
            Add --no-launch to run only the server without starting the game.
            Gameplay repairs are included automatically by default.
            Add --no-gameplay-patches to launch with connection/licensing patches only, for external gameplay mods.
            Pass game arguments after --, for example: Dollhouse.exe -- -windowed
            Or use --clean to delete saves, settings, local accounts/profiles and caches, then exit.
            --clean accepts a target path but cannot be combined with other modes or game arguments.
            Close the game and server first. Deleted progress cannot be recovered by this tool.
            Windows caches/device identity are shared by standalone copies for this user.
            Restores the original game EXE, then deletes local TLS/signing identities too.
            Verified old canonical EXE backups are restored if needed, then removed. Installed content is preserved.

            Binary verification, secure connection, patches, and local server are set up automatically.
            The game starts after the server is ready, using the Fallen Doll.cmd arguments.
            Dollhouse exits when the game closes, unless --no-launch is used.
            Keep the window open to see requests. Ctrl+C stops the server.
            """);
        return 0;
    }
    var options = AppOptions.Parse(args);
    debugStartup = options.Debug;
    Operation("find the game", options.Target);
    var installation = GameInstallation.Locate(options.Target, AppContext.BaseDirectory, Environment.CurrentDirectory);
    Operation("acquire the Dollhouse instance lock");
    using var instance = new Mutex(false, OperatingSystem.IsWindows() ? "Local\\Dollhouse-Loopback-7900" : "Dollhouse-Loopback-7900");
    var ownsMutex = false;
    try
    {
        try { ownsMutex = instance.WaitOne(0); }
        catch (AbandonedMutexException) { ownsMutex = true; }
        if (!ownsMutex) throw new InvalidOperationException("Dollhouse is already running. Use its existing window.");
        if (options.Clean)
        {
            Operation("clean generated game data", installation.Root);
            TerminalOutput.Line("  DOLLHOUSE — CLEAN", ConsoleColor.Cyan);
            GameCleanup.Run(installation, TerminalOutput.Preparation);
            TerminalOutput.Success("Cleanup complete. Run Dollhouse normally when ready.");
            return 0;
        }
        Operation("check whether the game is already running", installation.Executable);
        if (!options.NoLaunch) installation.EnsureNotRunning();
        // Check before writing files. Never take over another process's listening port.
        Operation("check the local HTTPS port", "127.0.0.1:7900");
        using (var check = new Socket(SocketType.Stream, ProtocolType.Tcp))
        {
            try { check.Bind(new IPEndPoint(IPAddress.Loopback, 7900)); }
            catch (SocketException error) when (error.SocketErrorCode == SocketError.AddressAlreadyInUse)
            { throw new InvalidOperationException("Port 7900 is already in use. Close the other server and try again.", error); }
        }
        TerminalOutput.Line();
        TerminalOutput.Line("  DOLLHOUSE", ConsoleColor.Cyan);
        TerminalOutput.Detail($"  Game: {installation.Executable}");
        using var prepared = GamePreparation.Prepare(installation, TerminalOutput.Preparation,
            options.Debug ? TerminalOutput.Preparation : null, Operation, TerminalOutput.BinaryVerification,
            retainVerifiedBinary: !options.NoLaunch);
        Operation("load the bundled content key");
        var pakKey = new PakKeyEndpoint(BundledContentKey.Read());
        Operation("load local accounts", Path.Combine(prepared.Paths.Local, "auth", "accounts.json"));
        var accounts = new LocalAccountStore(prepared.Paths);
        var email = new UserEmailEndpoint(accounts);
        var shop = new ShopEndpoint(accounts);
        Operation("load local profiles", Path.Combine(prepared.Paths.Local, "profiles"));
        var profiles = new AccountsEndpoint(accounts, new LocalProfileStore(prepared.Paths));
        var challenges = new LicenseChallengeStore();
        DebugRequestCapture? capture = null;
        if (options.Debug)
        {
            Operation("prepare debug captures", Path.Combine(prepared.Paths.Local, "debug"));
            capture = new DebugRequestCapture(prepared.Paths);
            TerminalOutput.Detail($"  Debug captures: {capture.SessionDirectory}");
            TerminalOutput.Warning("  DEBUG captures include credentials. Curl replay is manual.");
        }
        if (!pakKey.IsConfigured)
            TerminalOutput.Warning("PAK key unavailable in this build; /api/v1/pakkey will return 503.");
        Operation("configure the HTTPS server", prepared.Paths.Tls);
        var server = ServerApplication.Build(prepared.Paths, journal: new TerminalRequestJournal(), capture: capture,
            routes: [pakKey.Route, new NoticeEndpoint().Route, new UserHintEndpoint().Route, new UserPowEndpoint().Route,
                new UserLoginEndpoint(accounts).Route, new UserMeEndpoint(accounts).Route,
                email.GetRoute, email.PostRoute,
                shop.PricesRoute, shop.OpenRoute,
                .. profiles.Routes,
                new LicenseKeysetEndpoint(prepared.LicenseAuthority).Route,
                new LicenseChallengeEndpoint(accounts, challenges).Route,
                new LicenseLeaseEndpoint(accounts, challenges, prepared.LicenseAuthority).Route,
                new ManifestEndpoint().Route, new MailEndpoint().Route, new ClientReportEndpoint().Route],
            status: _ => { });
        try
        {
            // The process-wide mutex belongs to this thread; keep the console entry point synchronous.
            Operation("start the HTTPS server", "127.0.0.1:7900");
            server.StartAsync().GetAwaiter().GetResult();
            TerminalOutput.Line();
            TerminalOutput.Success("[4/4] Ready.");
            TerminalOutput.Detail($"  Listening on {string.Join(", ", server.Urls)}");
            TerminalOutput.Detail("  Keep this window open. Ctrl+C stops the server.");
            if (options.NoLaunch) TerminalOutput.Detail("  Server only.");
            if (!options.NoLaunch) TerminalOutput.Detail("  Launching game...");
            if (options.NoGameplayPatches) TerminalOutput.Detail("  Gameplay patches disabled; connection/licensing patches enabled.");
            TerminalOutput.Line();
            TerminalOutput.Detail("  TIME      METHOD  ROUTE  ENDPOINT");
            if (!options.NoLaunch)
            {
                Operation("launch the game", installation.Executable);
                using var game = GameLauncher.Start(prepared, options.GameArguments, options.NoGameplayPatches);
                Operation("wait for the game to close", installation.Executable);
                GameSessionLifetime.WaitAsync(game, server.Services.GetRequiredService<IHostApplicationLifetime>())
                    .GetAwaiter().GetResult();
            }
            Operation("stop the HTTPS server", "127.0.0.1:7900");
            server.WaitForShutdownAsync().GetAwaiter().GetResult();
        }
        finally { server.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        return 0;
    }
    finally { if (ownsMutex) instance.ReleaseMutex(); }
}
catch (Exception error) when (error is not OutOfMemoryException)
{
    foreach (var line in StartupErrors.Describe(startupOperation, startupPath, error, debugStartup))
        TerminalOutput.Error(line);
    if (pauseOnError)
    {
        Console.WriteLine("Press Enter to close this window.");
        Console.ReadLine();
    }
    return 1;
}
