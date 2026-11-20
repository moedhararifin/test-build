using System.Runtime.InteropServices;
using Dollhouse.Gameplay;

namespace Dollhouse.Tests;

/// <summary>Own native stubs model the real dispatcher ABI; no game code is loaded.</summary>
internal sealed unsafe class BulgeEligibilityFixture : IDisposable
{
    private const nint Engine = 101, Module = 102, PerformerModule = 103, PerformerType = 104;
    private static BulgeEligibilityFixture Current = null!;
    private readonly List<nint> allocations = [];
    private readonly List<string> logs = [];
    private readonly nint context, helper, update, stack, precompiled;
    private readonly NativeGame game;
    private uint parameters = 1, argumentFlags = 4;
    private int returnType = 1, argumentType = 4, originalCalls;
    private string helperName = "CheckHCardAvailableForBulge", helperNamespace = "HM_HGroup", helperOwner = "";
    private bool foreignModule, missingPerformer, failMetadata;
    private bool missingHelper, failNamespace;
    private string defaultNamespace = "Original";
    private nint Allocate(int size) { nint p = (nint)NativeMemory.AllocZeroed((nuint)size); allocations.Add(p); return p; }
    private nint Text(string value)
    {
        byte[] text = System.Text.Encoding.UTF8.GetBytes(value + '\0');
        nint p = Allocate(text.Length); text.CopyTo(new Span<byte>((void*)p, text.Length)); return p;
    }
    private BulgeEligibilityFixture()
    {
        Current = this;
        context = Allocate(0x1b0); helper = Allocate(0x180); update = Allocate(0x180); stack = Allocate(16);
        precompiled = Allocate(0x400); *(nint*)(precompiled + SupportedBuild.PrecompiledEngine) = Engine;
        *(nint*)(context + SupportedBuild.ScriptContextEngine) = Engine;
        *(nint*)(helper + SupportedBuild.ScriptFunctionEngine) = Engine;
        *(nint*)(helper + SupportedBuild.ScriptFunctionModule) = Module;
        *(int*)(helper + SupportedBuild.ScriptArgumentWords) = 1;
        *(int*)(helper + SupportedBuild.ScriptFunctionKind) = 1;
        *(int*)(helper + SupportedBuild.ScriptFunctionId) = 2345; // Deliberately differs from the cached ID.
        game = new(0, rva => rva switch
        {
            SupportedBuild.ScriptDispatch => (nint)(delegate* unmanaged<nint, nint, void>)&Original,
            SupportedBuild.ScriptFunctionName when !failMetadata => (nint)(delegate* unmanaged<nint, nint>)&FunctionName,
            SupportedBuild.ScriptFunctionNamespace => (nint)(delegate* unmanaged<nint, nint>)&Namespace,
            SupportedBuild.ScriptFunctionObjectName => (nint)(delegate* unmanaged<nint, nint>)&Owner,
            SupportedBuild.ScriptGetModule => (nint)(delegate* unmanaged<nint, byte*, int, nint>)&GetModule,
            SupportedBuild.ScriptModuleGetNamespace => (nint)(delegate* unmanaged<nint, nint>)&GetNamespace,
            SupportedBuild.ScriptModuleSetNamespace => (nint)(delegate* unmanaged<nint, nint, int>)&SetNamespace,
            SupportedBuild.ScriptModuleFunctionByName => (nint)(delegate* unmanaged<nint, byte*, nint>)&FindFunction,
            SupportedBuild.ScriptModuleEngine or SupportedBuild.ScriptTypeEngine => (nint)(delegate* unmanaged<nint, nint>)&GetEngine,
            SupportedBuild.ScriptModuleFindType => (nint)(delegate* unmanaged<nint, byte*, nint>)&FindType,
            SupportedBuild.ScriptTypeName => (nint)(delegate* unmanaged<nint, nint>)&TypeName,
            SupportedBuild.ScriptMethodByName => (nint)(delegate* unmanaged<nint, byte*, nint>)&Method,
            SupportedBuild.ScriptParameterCount => (nint)(delegate* unmanaged<nint, uint>)&ParameterCount,
            SupportedBuild.ScriptReturnType => (nint)(delegate* unmanaged<nint, uint*, int>)&ReturnType,
            SupportedBuild.ScriptParameter => (nint)(delegate* unmanaged<nint, uint, int*, uint*, nint*, nint*, int>)&Parameter,
            SupportedBuild.GameplayLog => (nint)(delegate* unmanaged<char*, void>)&Log,
            _ => throw new InvalidOperationException("Unexpected eligibility native API.")
        });
        game.InitializeBulgeEligibility(precompiled);
        Check.Equal("Original", defaultNamespace);
    }
    public void Dispose() { foreach (var p in allocations) NativeMemory.Free((void*)p); Current = null!; }
    private void Enabled(bool enabled)
    {
        fixed (char* key = SettingsBindings.IgnoreRestrictionsKey)
        fixed (char* text = enabled ? "1" : "0")
        {
            GameString value = new() { Data = text, Length = 2, Capacity = 2 };
            game.SetSetting(key, &value);
        }
    }
    private void ResetCall()
    {
        *(int*)(context + SupportedBuild.ScriptContextState) = 6;
        *(nint*)(context + SupportedBuild.ScriptStackPointer) = stack;
        *(ulong*)(context + SupportedBuild.ScriptValueRegister) = ulong.MaxValue;
        *(int*)stack = 147; *(int*)(stack + 4) = 0x12345678;
    }
    private void AssertAllowed(bool allowed, int before)
    {
        nint previousStack = *(nint*)(context + SupportedBuild.ScriptStackPointer);
        byte[] beforeContext = new ReadOnlySpan<byte>((void*)context, 0x1b0).ToArray();
        game.DispatchBulgeEligibility(context, helper);
        Check.Equal(allowed ? before : before + 1, originalCalls);
        Check.Equal(allowed ? 1UL : 0UL, *(ulong*)(context + SupportedBuild.ScriptValueRegister));
        Check.Equal(previousStack + 4, *(nint*)(context + SupportedBuild.ScriptStackPointer));
        Check.Equal(147, *(int*)stack); Check.Equal(0x12345678, *(int*)(stack + 4));
        byte[] after = new ReadOnlySpan<byte>((void*)context, 0x1b0).ToArray();
        foreach (int field in new[] { SupportedBuild.ScriptStackPointer, SupportedBuild.ScriptValueRegister })
        { beforeContext.AsSpan(field, 8).Clear(); after.AsSpan(field, 8).Clear(); }
        Check.True(beforeContext.AsSpan().SequenceEqual(after), "Dispatcher changed VM state, PC or frame outside its scalar return contract.");
    }
    internal static void Verify()
    {
        using var workspace = new TemporaryWorkspace();
        string? previous = Environment.GetEnvironmentVariable(SettingsBindings.PathVariable);
        Environment.SetEnvironmentVariable(SettingsBindings.PathVariable, Path.Combine(workspace.Paths.Root, "eligibility.json"));
        try
        {
            using (var f = new BulgeEligibilityFixture())
            {
                f.ResetCall(); f.failMetadata = true; f.AssertAllowed(false, 0); // OFF never touches reflection.
                f.Enabled(true); f.ResetCall(); f.AssertAllowed(true, 1); // Production dispatch never re-enters metadata APIs.
                Check.Equal(6, *(int*)(f.context + SupportedBuild.ScriptContextState));
                Check.True(!f.logs.Any(x => x.Contains("eligibility guard ready") || x.Contains("eligibility override:")),
                    "Successful setup/dispatch should not produce investigation logs.");
                f.Enabled(false); f.ResetCall(); f.AssertAllowed(false, 1); // Live toggle restores original immediately.
                *(int*)(f.helper + SupportedBuild.ScriptFunctionId) = 999;
                f.Enabled(true); f.helperName = "OtherBoolFunction"; f.ResetCall(); f.AssertAllowed(false, 2);
                f.failMetadata = true; f.ResetCall(); f.AssertAllowed(false, 3); // Other IDs do no metadata work.
            }
            foreach (string failure in new[] { "namespace", "receiver", "count", "return", "arg-type", "reference", "engine", "module", "kind", "words", "inactive", "null-stack", "unaligned-stack", "helper-vm", "helper-parameter", "helper-raw", "caller-vm", "caller-parameter", "caller-raw", "caller-missing", "exception" })
            {
                using var f = new BulgeEligibilityFixture(); f.Enabled(true); f.ResetCall();
                switch (failure)
                {
                    case "namespace": f.helperNamespace = "Other"; break;
                    case "receiver": f.helperOwner = "SomeObject"; break;
                    case "count": f.parameters = 2; break;
                    case "return": f.returnType = 4; break;
                    case "arg-type": f.argumentType = 1; break;
                    case "reference": f.argumentFlags = 7; break;
                    case "engine": *(nint*)(f.helper + SupportedBuild.ScriptFunctionEngine) = 999; break;
                    case "module": f.foreignModule = true; break;
                    case "kind": *(int*)(f.helper + SupportedBuild.ScriptFunctionKind) = 0; break;
                    case "words": *(int*)(f.helper + SupportedBuild.ScriptArgumentWords) = 2; break;
                    case "inactive": *(int*)(f.context + SupportedBuild.ScriptContextState) = 0; break;
                    case "null-stack": *(nint*)(f.context + SupportedBuild.ScriptStackPointer) = 0; break;
                    case "unaligned-stack": *(nint*)(f.context + SupportedBuild.ScriptStackPointer) = f.stack + 1; break;
                    case "caller-missing": f.missingPerformer = true; break;
                    case "exception": f.failMetadata = true; break;
                    default:
                        int offset = failure.EndsWith("-vm") ? SupportedBuild.ScriptVmEntry : failure.EndsWith("-raw") ? SupportedBuild.ScriptRawEntry : SupportedBuild.ScriptParameterEntry;
                        *(nint*)((failure.StartsWith("helper-") ? f.helper : f.update) + offset) = 999;
                        break;
                }
                if (failure is "namespace" or "receiver" or "count" or "return" or "arg-type" or "reference" or
                    "module" or "caller-vm" or "caller-parameter" or "caller-raw" or "caller-missing" or "exception")
                    f.game.InitializeBulgeEligibility(f.precompiled);
                f.AssertAllowed(false, 0);
                Check.True(f.logs.Any(x => x.Contains("original dispatch preserved")), "Rejected eligibility guard lacked diagnostics: " + failure);
                f.ResetCall();
                if (failure == "inactive") *(int*)(f.context + SupportedBuild.ScriptContextState) = 0;
                if (failure == "null-stack") *(nint*)(f.context + SupportedBuild.ScriptStackPointer) = 0;
                if (failure == "unaligned-stack") *(nint*)(f.context + SupportedBuild.ScriptStackPointer) = f.stack + 1;
                f.AssertAllowed(false, 1);
                Check.Equal(1, f.logs.Count(x => x.Contains("eligibility override refused"))); // No repeated rejection spam.
            }
            foreach (string failure in new[] { "helper-native", "caller-native", "missing", "namespace" })
            {
                using var f = new BulgeEligibilityFixture(); f.Enabled(true);
                if (failure == "helper-native") *(nint*)(f.helper + SupportedBuild.ScriptRawEntry) = 999;
                if (failure == "caller-native") *(nint*)(f.update + SupportedBuild.ScriptVmEntry) = 999;
                f.missingHelper = failure == "missing"; f.failNamespace = failure == "namespace";
                f.game.InitializeBulgeEligibility(f.precompiled);
                Check.Equal("Original", f.defaultNamespace);
                f.ResetCall(); f.failMetadata = true; f.AssertAllowed(false, 0); // Unready hook always forwards.
                Check.True(f.logs.Any(x => x.Contains("eligibility override refused")), "Missing initialization failure log.");
            }
            using (var f = new BulgeEligibilityFixture())
            { f.ResetCall(); f.AssertAllowed(true, 0); } // Persisted ON plus a fresh, successful startup guard.
        }
        finally { Environment.SetEnvironmentVariable(SettingsBindings.PathVariable, previous); }
    }
    [UnmanagedCallersOnly] private static void Original(nint context, nint function)
    { Current.originalCalls++; *(ulong*)(context + SupportedBuild.ScriptValueRegister) = 0; *(nint*)(context + SupportedBuild.ScriptStackPointer) += 4; }
    [UnmanagedCallersOnly] private static nint FunctionName(nint function) => Current.Text(function == Current.helper ? Current.helperName : "UpdateBulge");
    [UnmanagedCallersOnly] private static nint Namespace(nint function) => Current.Text(Current.helperNamespace);
    [UnmanagedCallersOnly] private static nint Owner(nint function) => Current.Text(function == Current.helper ? Current.helperOwner : "A_HPerformer");
    [UnmanagedCallersOnly] private static nint GetModule(nint engine, byte* name, int flags)
    {
        Check.Equal(Engine, engine); Check.Equal(0, flags);
        string module = Marshal.PtrToStringUTF8((nint)name)!;
        return module == "Multiplayer.BP.HM_HGroup" ? Current.foreignModule ? 999 : Module : Current.missingPerformer ? 0 : PerformerModule;
    }
    [UnmanagedCallersOnly] private static nint GetEngine(nint value) => Engine;
    [UnmanagedCallersOnly] private static nint GetNamespace(nint module) => Current.Text(Current.defaultNamespace);
    [UnmanagedCallersOnly] private static int SetNamespace(nint module, nint value)
    {
        string name = Marshal.PtrToStringUTF8(value)!;
        if (Current.failNamespace && name == "HM_HGroup") return -1;
        Current.defaultNamespace = name; return 0;
    }
    [UnmanagedCallersOnly] private static nint FindFunction(nint module, byte* name)
    {
        Check.Equal(Current.foreignModule ? (nint)999 : Module, module); Check.Equal("HM_HGroup", Current.defaultNamespace);
        Check.Equal("CheckHCardAvailableForBulge", Marshal.PtrToStringUTF8((nint)name)!);
        return Current.missingHelper ? 0 : Current.helper;
    }
    [UnmanagedCallersOnly] private static nint FindType(nint module, byte* name) => PerformerType;
    [UnmanagedCallersOnly] private static nint TypeName(nint type) => Current.Text("A_HPerformer");
    [UnmanagedCallersOnly] private static nint Method(nint type, byte* name) => Current.update;
    [UnmanagedCallersOnly] private static uint ParameterCount(nint function) => function == Current.helper ? Current.parameters : 0;
    [UnmanagedCallersOnly] private static int ReturnType(nint function, uint* flags)
    { if (flags != null) *flags = 0; return function == Current.helper ? Current.returnType : 0; }
    [UnmanagedCallersOnly] private static int Parameter(nint function, uint index, int* id, uint* flags, nint* name, nint* defaultValue)
    { *id = Current.argumentType; *flags = Current.argumentFlags; return 0; }
    [UnmanagedCallersOnly] private static void Log(char* text) => Current.logs.Add(new string(text));
}
