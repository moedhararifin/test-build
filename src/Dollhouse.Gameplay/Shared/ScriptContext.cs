using System.Runtime.InteropServices;

namespace Dollhouse.Gameplay;

internal sealed unsafe partial class NativeGame
{
    [StructLayout(LayoutKind.Explicit, Size = 0x10)]
    internal struct ScriptContextScope
    {
        [FieldOffset(0)] public nint Context;
        [FieldOffset(8)] public byte Nested;
    }

    // Match the native pooled/nested context lifetime, including restoration when a
    // callback fails. No engine object or method pointer escapes the synchronous call.
    private bool WithScriptContext(nint world, Func<nint, nint, bool> operation)
    {
        if (world == 0) return false;
        ScriptContextScope scope = default;
        nint* current = (nint*)At(SupportedBuild.ScriptWorldContext);
        nint previous = *current;
        ((delegate* unmanaged<ScriptContextScope*, nint>)At(SupportedBuild.ScriptScopeCreate))(&scope);
        try
        {
            *current = world;
            if (scope.Context == 0) return ScriptCallFailed("context", -1);
            nint engine = ((delegate* unmanaged<nint, nint>)At(SupportedBuild.ScriptGetEngine))(scope.Context);
            if (engine == 0) return ScriptCallFailed("engine lookup", -1);
            return operation(scope.Context, engine);
        }
        finally
        {
            try { if (scope.Context != 0) ((delegate* unmanaged<ScriptContextScope*, void>)At(SupportedBuild.ScriptScopeRelease))(&scope); }
            finally { *current = previous; }
        }
    }

    private bool PrepareScriptCall(nint context, nint function, nint receiver, string operation)
    {
        int status = ((delegate* unmanaged<nint, nint, int>)At(SupportedBuild.ScriptPrepare))(context, function);
        if (status < 0) return ScriptCallFailed(operation + " preparation", status);
        if (receiver == 0) return true; // Global function.
        status = ((delegate* unmanaged<nint, nint, int>)At(SupportedBuild.ScriptSetObject))(context, receiver);
        return status >= 0 || ScriptCallFailed(operation + " receiver", status);
    }

    private bool ExecuteScriptCall(nint context, string operation)
    {
        int status = ((delegate* unmanaged<nint, int>)At(SupportedBuild.ScriptExecute))(context);
        if (status == 0) return true;
        if (status == 3) LogScriptException(context);
        return ScriptCallFailed(operation + " execution", status);
    }

    private bool ScriptCallFailed(string stage, int status)
    {
        LogCumflation($"script {stage} failed (status={status})");
        return false;
    }

    private void LogScriptException(nint context, Action<string>? log = null)
    {
        string error = ScriptString(((delegate* unmanaged<nint, nint>)At(SupportedBuild.ScriptExceptionString))(context));
        int column = 0;
        nint section = 0;
        int line = ((delegate* unmanaged<nint, int*, nint*, int>)At(SupportedBuild.ScriptExceptionLine))(context, &column, &section);
        (log ?? LogCumflation)($"script exception: {ScriptString(section)}:{line}:{column}: {error}");
    }
}
