namespace Dollhouse.Gameplay;

internal sealed unsafe partial class NativeGame
{
    private bool ParseCameraPresets(string source, CameraArray* output)
    {
        // The parser borrows both references. Keep the string pinned until its
        // nested context is released; only the game allocates/frees the array.
        fixed (char* text = source)
        {
            GameString json = new() { Data = text, Length = source.Length + 1, Capacity = source.Length + 1 };
            ScriptContextScope scope = default;
            nint* world = (nint*)At(SupportedBuild.ScriptWorldContext);
            nint previousWorld = *world;
            try
            {
                ((delegate* unmanaged<ScriptContextScope*, nint>)At(SupportedBuild.ScriptScopeCreate))(&scope);
                if (scope.Context == 0) return CameraParserFailed("context acquisition", -1);
                nint engine = ((delegate* unmanaged<nint, nint>)At(SupportedBuild.ScriptGetEngine))(scope.Context);
                if (engine == 0) return CameraParserFailed("engine lookup", -1);
                nint parser = FindCameraParser(engine);
                return parser != 0 && ExecuteCameraParser(scope.Context, parser, &json, output);
            }
            finally
            {
                try
                {
                    if (scope.Context != 0)
                        ((delegate* unmanaged<ScriptContextScope*, void>)At(SupportedBuild.ScriptScopeRelease))(&scope);
                }
                finally { *world = previousWorld; }
            }
        }
    }

    private bool ExecuteCameraParser(nint context, nint parser, GameString* json, CameraArray* output)
    {
        int status = ((delegate* unmanaged<nint, nint, int>)At(SupportedBuild.ScriptPrepare))(context, parser);
        if (status < 0) return CameraParserFailed("preparation", status);
        status = ((delegate* unmanaged<nint, uint, nint, int>)At(SupportedBuild.ScriptSetArgAddress))(context, 0, (nint)json);
        if (status < 0) return CameraParserFailed("JSON argument", status);
        status = ((delegate* unmanaged<nint, uint, nint, int>)At(SupportedBuild.ScriptSetArgAddress))(context, 1, (nint)output);
        if (status < 0) return CameraParserFailed("output argument", status);
        status = ((delegate* unmanaged<nint, int>)At(SupportedBuild.ScriptExecute))(context);
        if (status == 3) LogScriptException(context, LogCamera);
        return status == 0 || CameraParserFailed("execution", status);
    }

    private bool CameraParserFailed(string stage, int status)
    {
        LogCamera($"parser {stage} failed (status={status})");
        return false;
    }
}
