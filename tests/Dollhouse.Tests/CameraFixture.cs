using System.Runtime.InteropServices;
using System.Text.Json;
using Dollhouse.Gameplay;

namespace Dollhouse.Tests;

// Entirely analyst-written native ABI doubles. No game module is loaded/executed.
internal sealed unsafe class CameraFixture : IDisposable
{
    private static CameraFixture Current = null!;
    private const nint Engine = 0x1000, Module = 0x2000, Context = 0x3000;
    private readonly List<nint> allocations = [];
    private readonly Dictionary<string, nint> strings = [];
    private readonly List<string> logs = [];
    private readonly nint loader, parser, precompiled, world;
    private bool missingModule, wrongNamespace, missingContext, missingEngine;
    private bool missingLoader, missingParser;
    private bool missingNamespace, failNamespaceSelection;
    private string moduleNamespace = "ExistingNamespace";
    private int loaderLookups, parserLookups;
    private int prepareStatus, executeStatus, releases, executions, failedArgument = -1;
    private GameString* json;
    private NativeGame.CameraArray* output;

    private nint Allocate(int size)
    { nint value = (nint)NativeMemory.AllocZeroed((nuint)size); allocations.Add(value); return value; }

    private CameraFixture()
    {
        Current = this;
        loader = Allocate(0x180); parser = Allocate(0x180);
        precompiled = Allocate(0x400); *(nint*)(precompiled + SupportedBuild.PrecompiledEngine) = Engine;
        world = Allocate(8); *(nint*)world = 0x1234;
    }

    private nint Text(string text)
    {
        if (!strings.TryGetValue(text, out nint value)) strings[text] = value = Marshal.StringToCoTaskMemUTF8(text);
        return value;
    }

    private NativeGame Game() => new(0, rva => rva switch
    {
        SupportedBuild.GameplayLog => (nint)(delegate* unmanaged<char*, void>)&Log,
        SupportedBuild.ScriptWorldContext => world,
        SupportedBuild.ScriptGetModule => (nint)(delegate* unmanaged<nint, byte*, int, nint>)&GetModule,
        SupportedBuild.ScriptModuleEngine => (nint)(delegate* unmanaged<nint, nint>)&GetEngine,
        SupportedBuild.ScriptGetEngine => (nint)(delegate* unmanaged<nint, nint>)&GetEngine,
        SupportedBuild.ScriptModuleFunctionByName => (nint)(delegate* unmanaged<nint, byte*, nint>)&FunctionByName,
        SupportedBuild.ScriptModuleGetNamespace => (nint)(delegate* unmanaged<nint, nint>)&GetNamespace,
        SupportedBuild.ScriptModuleSetNamespace => (nint)(delegate* unmanaged<nint, byte*, int>)&SetNamespace,
        SupportedBuild.ScriptFunctionNamespace => (nint)(delegate* unmanaged<nint, nint>)&Namespace,
        SupportedBuild.ScriptScopeCreate => (nint)(delegate* unmanaged<NativeGame.ScriptContextScope*, nint>)&Acquire,
        SupportedBuild.ScriptScopeRelease => (nint)(delegate* unmanaged<NativeGame.ScriptContextScope*, void>)&Release,
        SupportedBuild.ScriptPrepare => (nint)(delegate* unmanaged<nint, nint, int>)&Prepare,
        SupportedBuild.ScriptSetArgAddress => (nint)(delegate* unmanaged<nint, uint, nint, int>)&SetArgument,
        SupportedBuild.ScriptExecute => (nint)(delegate* unmanaged<nint, int>)&Execute,
        SupportedBuild.ScriptExceptionString => (nint)(delegate* unmanaged<nint, nint>)&Exception,
        SupportedBuild.ScriptExceptionLine => (nint)(delegate* unmanaged<nint, int*, nint*, int>)&Line,
        _ => throw new InvalidOperationException($"Unexpected native call {rva:x}.")
    });

    public void Dispose()
    {
        foreach (nint text in strings.Values) Marshal.FreeCoTaskMem(text);
        foreach (nint value in allocations) NativeMemory.Free((void*)value);
        Current = null!;
    }

    internal static void Verify()
    {
        using var directory = new TemporaryWorkspace();
        string path = Path.Combine(directory.Paths.Root, "camera Ω.config");
        File.WriteAllText(path, "[{},{}]");
        string? previous = Environment.GetEnvironmentVariable(CameraBindings.PathVariable);
        Environment.SetEnvironmentVariable(CameraBindings.PathVariable, path);
        try
        {
            using (var f = new CameraFixture())
            {
                var game = f.Game(); game.InstallCameraLoader(f.precompiled, 0x7777);
                Check.Equal((nint)0x7777, *(nint*)(f.loader + SupportedBuild.ScriptVmEntry));
                Check.Equal((nint)0, *(nint*)(f.parser + SupportedBuild.ScriptVmEntry));
                game.InstallCameraLoader(f.precompiled, 0x7777); // Idempotent installation.
                f.Invoke(game, 2); f.Invoke(game, 2); Check.Equal(2, f.executions); Check.Equal(2, f.releases);
                Check.True(f.logs.Any(x => x.Contains("camera count=2")), "Parsed count was not logged.");
                Check.Equal(2, f.logs.Count); // One useful result per load; no installation/probe chatter.
                Check.Equal(2, f.loaderLookups); // Installation only; loads resolve just the parser.
                Check.Equal(4, f.parserLookups);
                Check.Equal("ExistingNamespace", f.moduleNamespace);
            }
            foreach (int offset in new[] { SupportedBuild.ScriptVmEntry, SupportedBuild.ScriptParameterEntry, SupportedBuild.ScriptRawEntry })
            {
                using var f = new CameraFixture(); *(nint*)(f.loader + offset) = 0x9999;
                f.Game().InstallCameraLoader(f.precompiled, 0x7777);
                Check.True(*(nint*)(f.loader + SupportedBuild.ScriptVmEntry) != 0x7777, "Unsupported entry was overwritten.");
                Check.True(f.logs.Any(x => x.Contains("installation refused")), "Unsupported execution entry was not diagnosed.");
            }
            foreach (string fault in new[] { "module", "parser", "namespace", "namespaceMissing", "namespaceSelection", "context", "engine", "prepare", "argument0", "argument1", "execute" })
            {
                using var f = new CameraFixture();
                f.missingModule = fault == "module"; f.wrongNamespace = fault == "namespace";
                f.missingParser = fault == "parser";
                f.missingNamespace = fault == "namespaceMissing"; f.failNamespaceSelection = fault == "namespaceSelection";
                f.missingContext = fault == "context"; f.missingEngine = fault == "engine";
                f.failedArgument = fault == "argument0" ? 0 : fault == "argument1" ? 1 : -1;
                if (fault == "prepare") f.prepareStatus = -1;
                if (fault == "execute") f.executeStatus = 3;
                f.Invoke(f.Game(), 1);
                Check.Equal(f.missingContext ? 0 : 1, f.releases);
                Check.True(f.logs.Any(x => x.Contains("failed") || x.Contains("rejected") || x.Contains("incomplete") || x.Contains("status=3")), "Failure was not diagnosed.");
                Check.True(f.logs.All(x => x.StartsWith("[Dollhouse camera]")), "Camera failure used another feature's log prefix.");
                Check.True(!f.logs.Any(x => x.Contains("presets loaded")), "Failed parser reported success.");
            }
            foreach (bool missingLoader in new[] { true, false })
            {
                using var f = new CameraFixture();
                f.missingLoader = missingLoader; f.missingParser = !missingLoader;
                f.Game().InstallCameraLoader(f.precompiled, 0x7777);
                Check.Equal((nint)0, *(nint*)(f.loader + SupportedBuild.ScriptVmEntry));
                Check.True(f.logs.Any(x => x.Contains("absent")), "Unresolved function was not diagnosed.");
                Check.Equal("ExistingNamespace", f.moduleNamespace);
            }
            using (var f = new CameraFixture())
            {
                f.missingLoader = true;
                f.Invoke(f.Game(), 2); // Parsing never depends on resolving the loader again.
                Check.Equal(0, f.loaderLookups); Check.Equal(1, f.parserLookups);
            }
            File.Delete(path);
            using (var f = new CameraFixture()) f.Invoke(f.Game(), 0);
            File.WriteAllText(path, "malformed JSON");
            using (var f = new CameraFixture()) f.Invoke(f.Game(), 0);
        }
        finally { Environment.SetEnvironmentVariable(CameraBindings.PathVariable, previous); }
    }

    private void Invoke(NativeGame game, int expected)
    {
        NativeGame.CameraArray array = new() { Data = 0x5555, Count = 1, Capacity = 10 };
        nint* args = stackalloc nint[] { 0xdead, (nint)(&array) }; // Filename is intentionally ignored, never freed/read.
        game.LoadCameraPositions((nint)args);
        Check.Equal(expected, array.Count); Check.Equal((nint)0x5555, array.Data);
        Check.Equal((nint)0x1234, *(nint*)world);
        Check.Equal((nint)0xdead, args[0]);
        Check.Equal("ExistingNamespace", moduleNamespace);
    }

    [UnmanagedCallersOnly] private static nint GetModule(nint engine, byte* name, int flags)
    { Check.Equal(Engine, engine); Check.Equal(0, flags); Check.Equal("SubSystem.SaveGame.SavedConfigIO", Marshal.PtrToStringUTF8((nint)name)!); return Current.missingModule ? 0 : Module; }
    [UnmanagedCallersOnly] private static nint GetEngine(nint value) => Current.missingEngine && value == Context ? 0 : Engine;
    [UnmanagedCallersOnly] private static nint GetNamespace(nint module) =>
        Current.missingNamespace ? 0 : Current.Text(Current.moduleNamespace);
    [UnmanagedCallersOnly] private static int SetNamespace(nint module, byte* name)
    {
        Check.Equal(Module, module);
        Current.moduleNamespace = Marshal.PtrToStringUTF8((nint)name)!;
        // Model a failing setter that has already changed state: finally must restore it.
        return Current.failNamespaceSelection && Current.moduleNamespace == "SavedConfigIO" ? -1 : 0;
    }
    [UnmanagedCallersOnly] private static nint FunctionByName(nint module, byte* name)
    {
        Check.Equal(Module, module);
        Check.Equal("SavedConfigIO", Current.moduleNamespace);
        string text = Marshal.PtrToStringUTF8((nint)name)!;
        if (text == "LoadCamPosition")
        { Current.loaderLookups++; return Current.missingLoader ? 0 : Current.loader; }
        Check.Equal("ParseCamConfJson", text);
        Current.parserLookups++;
        return Current.missingParser ? 0 : Current.parser;
    }
    [UnmanagedCallersOnly] private static nint Namespace(nint function) => Current.Text(Current.wrongNamespace ? "Other" : "SavedConfigIO");
    [UnmanagedCallersOnly] private static nint Acquire(NativeGame.ScriptContextScope* scope)
    {
        *(nint*)Current.world = 0xabcd; // Acquisition can alter ambient state, even without a context.
        scope->Context = Current.missingContext ? 0 : Context; scope->Nested = 1; return (nint)scope;
    }
    [UnmanagedCallersOnly] private static void Release(NativeGame.ScriptContextScope* scope) { Check.Equal(Context, scope->Context); Check.Equal((byte)1, scope->Nested); Current.releases++; }
    [UnmanagedCallersOnly] private static int Prepare(nint context, nint function)
    {
        Check.Equal(Context, context); Check.Equal(Current.parser, function);
        Check.Equal("ExistingNamespace", Current.moduleNamespace); // Restore before executing script.
        return Current.prepareStatus;
    }
    [UnmanagedCallersOnly] private static int SetArgument(nint context, uint index, nint address)
    {
        if (Current.failedArgument == (int)index) return -1;
        if (index == 0) Current.json = (GameString*)address; else Current.output = (NativeGame.CameraArray*)address;
        return 0;
    }
    [UnmanagedCallersOnly] private static int Execute(nint context)
    {
        *(nint*)Current.world = 0xbeef; // Nested context's ambient state must be restored even on exception.
        Current.executions++;
        if (Current.executeStatus != 0) return Current.executeStatus;
        Check.Equal('\0', Current.json->Data[Current.json->Length - 1]);
        string text = new(Current.json->Data, 0, Current.json->Length - 1);
        try { using var parsed = JsonDocument.Parse(text); Current.output->Count = parsed.RootElement.GetArrayLength(); }
        catch (JsonException) { Current.output->Count = 0; }
        return 0;
    }
    [UnmanagedCallersOnly] private static void Log(char* text) => Current.logs.Add(new string(text));
    [UnmanagedCallersOnly] private static nint Exception(nint context) => Current.Text("synthetic parser exception");
    [UnmanagedCallersOnly] private static int Line(nint context, int* column, nint* section) { *column = 1; *section = Current.Text("camera fixture"); return 1; }
}
