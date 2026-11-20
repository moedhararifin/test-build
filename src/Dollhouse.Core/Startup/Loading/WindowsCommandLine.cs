using System.Text;

namespace Dollhouse.Core.Startup;

/// <summary>Serializes ArgumentList for CreateProcessW, which accepts one UTF-16 command line.</summary>
internal static class WindowsCommandLine
{
    internal static StringBuilder Build(string executable, IEnumerable<string> arguments)
    {
        // argv[0] has different parsing rules from subsequent arguments. A
        // Windows executable path cannot contain quotes; always quote it whole.
        if (executable.Contains('"') || executable.Contains('\0'))
            throw new ArgumentException("Invalid executable path.", nameof(executable));
        var command = new StringBuilder().Append('"').Append(executable).Append('"');
        foreach (string argument in arguments)
        {
            if (argument.Contains('\0')) throw new ArgumentException("Arguments cannot contain NUL.", nameof(arguments));
            command.Append(' ');
            AppendArgument(command, argument);
        }
        return command;
    }

    private static void AppendArgument(StringBuilder command, string argument)
    {
        // Match .NET ProcessStartInfo.ArgumentList / CRT escaping:
        // https://github.com/dotnet/runtime/blob/main/src/libraries/System.Private.CoreLib/src/System/PasteArguments.cs
        // Simple switches stay unquoted, preserving Unreal's raw command line.
        if (argument.Length > 0 && !argument.Any(character => char.IsWhiteSpace(character) || character == '"'))
        {
            command.Append(argument);
            return;
        }

        command.Append('"');
        int backslashes = 0;
        foreach (char character in argument)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }
            if (character == '"')
            {
                // N literal backslashes followed by a literal quote require
                // 2N+1 backslashes before that quote on the command line.
                command.Append('\\', 2 * backslashes + 1);
            }
            else command.Append('\\', backslashes);
            command.Append(character);
            backslashes = 0;
        }
        // Double trailing backslashes so they cannot escape our closing quote.
        command.Append('\\', 2 * backslashes).Append('"');
    }
}
