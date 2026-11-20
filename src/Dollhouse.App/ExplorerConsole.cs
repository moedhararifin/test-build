using System.Runtime.InteropServices;

namespace Dollhouse.App;

internal static class ExplorerConsole
{
    public static bool IsStandalone => OperatingSystem.IsWindows() && !Console.IsInputRedirected
        && GetConsoleProcessList(new uint[2], 2) == 1;

    [DllImport("kernel32.dll")]
    private static extern uint GetConsoleProcessList([Out] uint[] processList, uint count);
}
