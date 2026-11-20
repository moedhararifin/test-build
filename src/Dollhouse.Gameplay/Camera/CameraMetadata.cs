namespace Dollhouse.Gameplay;

internal sealed unsafe partial class NativeGame
{
    private readonly record struct CameraMethods(nint Loader, nint Parser);

    private CameraMethods FindCameraMethods(nint engine)
    {
        nint module = FindCameraModule(engine);
        if (module == 0) return default;
        return new(
            FindCameraFunction(module, "LoadCamPosition\0"u8),
            FindCameraFunction(module, "ParseCamConfJson\0"u8));
    }

    private nint FindCameraParser(nint engine)
    {
        nint module = FindCameraModule(engine);
        return module == 0 ? 0 : FindCameraFunction(module, "ParseCamConfJson\0"u8);
    }

    private nint FindCameraModule(nint engine)
    {
        nint module;
        fixed (byte* name = "SubSystem.SaveGame.SavedConfigIO\0"u8)
            module = ((delegate* unmanaged<nint, byte*, int, nint>)At(SupportedBuild.ScriptGetModule))(engine, name, 0);
        if (module == 0 || ((delegate* unmanaged<nint, nint>)At(SupportedBuild.ScriptModuleEngine))(module) != engine)
        { LogCamera("method lookup failed: SavedConfigIO module unavailable or foreign engine"); return 0; }
        return module;
    }

    private nint FindCameraFunction(nint module, ReadOnlySpan<byte> name)
    {
        nint previous = ((delegate* unmanaged<nint, nint>)At(SupportedBuild.ScriptModuleGetNamespace))(module);
        if (previous == 0) { LogCamera("method lookup failed: module namespace unavailable"); return 0; }
        // Name lookup uses the module's default namespace. Restore it before any
        // script runs, even on failure. No parser, enumeration or pointer cache.
        try
        {
            fixed (byte* space = "SavedConfigIO\0"u8)
            {
                int status = ((delegate* unmanaged<nint, byte*, int>)At(SupportedBuild.ScriptModuleSetNamespace))(module, space);
                if (status < 0) { LogCamera($"method namespace selection failed (status={status})"); return 0; }
            }
            fixed (byte* text = name)
            {
                nint function = ((delegate* unmanaged<nint, byte*, nint>)At(SupportedBuild.ScriptModuleFunctionByName))(module, text);
                if (function == 0) { LogCamera($"method lookup failed: SavedConfigIO::{ScriptString((nint)text)} absent"); return 0; }
                string space = ScriptString(((delegate* unmanaged<nint, nint>)At(SupportedBuild.ScriptFunctionNamespace))(function));
                if (space != "SavedConfigIO")
                { LogCamera($"method lookup rejected: {ScriptString((nint)text)} resolved in namespace {space}"); return 0; }
                return function;
            }
        }
        finally
        {
            int status = ((delegate* unmanaged<nint, nint, int>)At(SupportedBuild.ScriptModuleSetNamespace))(module, previous);
            if (status < 0) LogCamera($"method namespace restoration failed (status={status})");
        }
    }
}
