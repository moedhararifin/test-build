namespace Dollhouse.App;

internal sealed record AppOptions(string? Target, bool Debug, bool Clean, bool NoLaunch, bool NoGameplayPatches, IReadOnlyList<string> GameArguments)
{
    public static AppOptions Parse(string[] arguments)
    {
        string? target = null;
        var debug = false;
        var clean = false;
        var noLaunch = false;
        var noGameplayPatches = false;
        var gameArguments = Array.Empty<string>();
        var hasGameArguments = false;
        for (var i = 0; i < arguments.Length; i++)
        {
            var argument = arguments[i];
            if (argument == "--")
            {
                hasGameArguments = true;
                gameArguments = arguments[(i + 1)..];
                break;
            }
            if (argument == "--debug")
            {
                if (debug) throw new ArgumentException("--debug was specified twice.");
                debug = true;
            }
            else if (argument == "--clean")
            {
                if (clean) throw new ArgumentException("--clean was specified twice.");
                clean = true;
            }
            else if (argument == "--no-launch")
            {
                if (noLaunch) throw new ArgumentException("--no-launch was specified twice.");
                noLaunch = true;
            }
            else if (argument == "--no-gameplay-patches")
            {
                if (noGameplayPatches) throw new ArgumentException("--no-gameplay-patches was specified twice.");
                noGameplayPatches = true;
            }
            else if (argument == "--target")
            {
                if (target is not null || ++i >= arguments.Length || arguments[i].StartsWith('-'))
                    throw new ArgumentException("Supply one game EXE path after --target.");
                target = arguments[i];
            }
            else if (!argument.StartsWith('-') && target is null) target = argument;
            else throw new ArgumentException("Usage: Dollhouse.exe [--debug] [--no-launch | --no-gameplay-patches] [\"C:\\path\\to\\game.exe\"] [-- game arguments], or Dollhouse.exe --clean [--target game.exe]");
        }
        if (clean && debug) throw new ArgumentException("--clean is exclusive and cannot be combined with --debug.");
        if (clean && (noLaunch || noGameplayPatches || hasGameArguments))
            throw new ArgumentException("--clean is exclusive and cannot be combined with launch options or game arguments.");
        if (noLaunch && gameArguments.Length != 0)
            throw new ArgumentException("Game arguments cannot be used with --no-launch.");
        if (noLaunch && noGameplayPatches)
            throw new ArgumentException("--no-gameplay-patches requires launching the game; it cannot be combined with --no-launch.");
        return new(target, debug, clean, noLaunch, noGameplayPatches, gameArguments);
    }
}
