namespace Dollhouse.Gameplay;

internal sealed unsafe partial class NativeGame
{
    // Gameplay paths report failures only. Settings also report load/committed state
    // changes. No account/session data or frame-by-frame diagnostics belong here.
    private void LogCumflation(string message)
    {
        try
        {
            fixed (char* text = "[Dollhouse cumflation] " + message)
                ((delegate* unmanaged<char*, void>)At(SupportedBuild.GameplayLog))(text);
        }
        catch (Exception) { } // A failed diagnostic must never interrupt native cleanup.
    }
}
