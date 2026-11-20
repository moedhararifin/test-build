using System.Runtime.InteropServices;

namespace Dollhouse.Gameplay;

internal sealed unsafe partial class NativeGame
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct CameraArray { internal nint Data; internal int Count, Capacity; }

    internal void InstallCameraLoader(nint precompiledData, nint replacement)
    {
        if (precompiledData == 0 || replacement == 0)
        { LogCamera("installation failed: missing precompiled data or replacement"); return; }
        nint engine = *(nint*)(precompiledData + SupportedBuild.PrecompiledEngine);
        if (engine == 0) { LogCamera("installation failed: script engine missing"); return; }
        var methods = FindCameraMethods(engine);
        if (methods.Loader == 0 || methods.Parser == 0) return;
        nint* entry = (nint*)(methods.Loader + SupportedBuild.ScriptVmEntry);
        bool parameterEntry = *(nint*)(methods.Loader + SupportedBuild.ScriptParameterEntry) != 0;
        bool rawEntry = *(nint*)(methods.Loader + SupportedBuild.ScriptRawEntry) != 0;
        // A different native entry could bypass this VM replacement. Do not claim
        // partial support or replace an unknown implementation/ABI in another build.
        if ((*entry != 0 && *entry != replacement) || parameterEntry || rawEntry)
        { LogCamera($"installation refused: unexpected native entries (vm={*entry != 0}, parameter={parameterEntry}, raw={rawEntry})"); return; }
        *entry = replacement;
    }

    internal void LoadCameraPositions(nint arguments)
    {
        // Global function, two borrowed references. No receiver or return object.
        // The engine dispatchers consume the four DWORD argument words after return.
        if (arguments == 0 || ((nint*)arguments)[1] == 0)
        { LogCamera("load rejected: missing output array reference"); return; }
        var output = (CameraArray*)((nint*)arguments)[1];
        if (output->Count < 0 || output->Capacity < output->Count || (output->Count > 0 && output->Data == 0))
        { LogCamera("load rejected: malformed output array"); return; }
        string json = CameraPresetFile.Read(Environment.GetEnvironmentVariable(CameraBindings.PathVariable), LogCamera);
        if (ParseCameraPresets(json, output))
            LogCamera($"presets loaded: camera count={output->Count}");
    }

    internal void LogCamera(string message)
    {
        try
        {
            fixed (char* text = "[Dollhouse camera] " + message)
                ((delegate* unmanaged<char*, void>)At(SupportedBuild.GameplayLog))(text);
        }
        catch { /* Diagnostics must not escape an engine callback. */ }
    }
}
