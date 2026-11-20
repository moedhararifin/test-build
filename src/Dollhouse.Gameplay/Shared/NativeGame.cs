using System.Runtime.InteropServices;

namespace Dollhouse.Gameplay;

// Shared module resolution and string ownership; feature-specific calls live in the partial files.
internal sealed unsafe partial class NativeGame
{
    private readonly nint image;
    private readonly Func<int, nint>? resolve;
    public NativeGame() : this(GetModuleHandleW(null), null) { }
    internal NativeGame(nint image, Func<int, nint>? resolve)
    {
        this.image = image;
        this.resolve = resolve;
    }
    private nint At(int rva) => resolve is null ? image + rva : resolve(rva);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern nint GetModuleHandleW(char* name);

    private void Copy(GameString* destination, GameString* source) =>
        ((delegate* unmanaged<GameString*, GameString*, nint>)At(SupportedBuild.CopyString))(destination, source);
    private void Free(GameString* value)
    {
        if (value->Data != null) ((delegate* unmanaged<void*, void>)At(SupportedBuild.Free))(value->Data);
    }

    private void LogGameplay(string feature, string message)
    {
        try
        {
            fixed (char* text = "[Dollhouse " + feature + "] " + message)
                ((delegate* unmanaged<char*, void>)At(SupportedBuild.GameplayLog))(text);
        }
        catch { /* Diagnostics must not escape a native callback. */ }
    }
}
