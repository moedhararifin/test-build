namespace Dollhouse.Gameplay;

public static unsafe partial class Exports
{
    // Export methods are ABI adapters only. Never let a managed exception cross into Unreal.
    // Lazy initialization occurs on the first gameplay call, never from DllMain/loader lock.
    private static readonly Lazy<NativeGame> Instance = new(() => new NativeGame());
    private static NativeGame Game => Instance.Value;

    private static void Report(Exception error) => Console.Error.WriteLine(
        $"[Dollhouse gameplay] Patch failed: {error.GetType().Name}");
}
