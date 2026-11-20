using System.Runtime.InteropServices;
using Dollhouse.Gameplay;

namespace Dollhouse.Tests;

internal sealed unsafe class DrinkEligibilityFixture : IDisposable
{
    private const nint Engine = 101, Module = 102, Type = 103;
    private static DrinkEligibilityFixture Current = null!;
    private readonly List<nint> allocations = [];
    private readonly List<string> logs = [];
    private readonly nint function, getter, precompiled, actor, nativeClass, context, world, row;
    private readonly NativeGame game;
    private bool ownerMissing, missingMethod, foreignModule;
    private int stamina = 3, cost = 3, releases, status;
    private nint prepared;

    private nint Allocate(int bytes)
    { nint p = (nint)NativeMemory.AllocZeroed((nuint)bytes); allocations.Add(p); return p; }
    private nint Text(string text)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(text + '\0');
        nint p = Allocate(bytes.Length); bytes.CopyTo(new Span<byte>((void*)p, bytes.Length)); return p;
    }
    private DrinkEligibilityFixture()
    {
        Current = this;
        function = Allocate(0x180); getter = Allocate(0x180); precompiled = Allocate(0x400);
        actor = Allocate(0x500); nativeClass = Allocate(0x80); context = Allocate(0x200); world = Allocate(8); row = Allocate(32);
        *(nint*)(precompiled + SupportedBuild.PrecompiledEngine) = Engine;
        *(nint*)(function + SupportedBuild.ScriptFunctionEngine) = Engine;
        *(int*)(function + SupportedBuild.ScriptArgumentWords) = 2;
        *(int*)(function + SupportedBuild.ScriptFunctionKind) = 1;
        *(nint*)(actor + SupportedBuild.ObjectClass) = nativeClass;
        *(nint*)(nativeClass + SupportedBuild.ClassAncestors) = Allocate(8);
        **(nint**)(nativeClass + SupportedBuild.ClassAncestors) = nativeClass + SupportedBuild.ClassAncestors;
        *(int*)(actor + SupportedBuild.DrinkOwnerRoleId) = 10001;
        *(nint*)world = 999;
        game = new(0, rva => rva switch
        {
            SupportedBuild.ScriptGetModule => (nint)(delegate* unmanaged<nint, byte*, int, nint>)&GetModule,
            SupportedBuild.ScriptModuleEngine or SupportedBuild.ScriptTypeEngine or SupportedBuild.ScriptGetEngine => (nint)(delegate* unmanaged<nint, nint>)&GetEngine,
            SupportedBuild.ScriptModuleFindType => (nint)(delegate* unmanaged<nint, byte*, nint>)&FindType,
            SupportedBuild.ScriptTypeName or SupportedBuild.ScriptFunctionObjectName => (nint)(delegate* unmanaged<nint, nint>)&TypeName,
            SupportedBuild.ScriptTypeNativeClass => (nint)(delegate* unmanaged<nint, nint>)&NativeClass,
            SupportedBuild.ScriptMethodByName => (nint)(delegate* unmanaged<nint, byte*, nint>)&Method,
            SupportedBuild.ScriptFunctionName => (nint)(delegate* unmanaged<nint, nint>)&FunctionName,
            SupportedBuild.ScriptParameterCount => (nint)(delegate* unmanaged<nint, uint>)&Parameters,
            SupportedBuild.ScriptReturnType => (nint)(delegate* unmanaged<nint, uint*, int>)&ReturnType,
            SupportedBuild.ScriptWorldContext => world,
            SupportedBuild.ScriptScopeCreate => (nint)(delegate* unmanaged<NativeGame.ScriptContextScope*, nint>)&Acquire,
            SupportedBuild.ScriptScopeRelease => (nint)(delegate* unmanaged<NativeGame.ScriptContextScope*, void>)&Release,
            SupportedBuild.ScriptPrepare => (nint)(delegate* unmanaged<nint, nint, int>)&Prepare,
            SupportedBuild.ScriptSetObject => (nint)(delegate* unmanaged<nint, nint, int>)&SetObject,
            SupportedBuild.ScriptExecute => (nint)(delegate* unmanaged<nint, int>)&Execute,
            SupportedBuild.ScriptReturnObject => (nint)(delegate* unmanaged<nint, nint>)&Result,
            SupportedBuild.FemaleById => (nint)(delegate* unmanaged<int, nint>)&Female,
            SupportedBuild.ReadStoredStamina => (nint)(delegate* unmanaged<GameString*, int*, int*, void>)&Read,
            SupportedBuild.ReadIntegerConfig => (nint)(delegate* unmanaged<char*, int, int>)&Config,
            SupportedBuild.DrinkStaminaCostKey => 1,
            SupportedBuild.GameplayLog => (nint)(delegate* unmanaged<char*, void>)&Log,
            _ => throw new InvalidOperationException($"Unexpected drink ABI {rva:x}.")
        });
    }
    public void Dispose() { foreach (nint p in allocations) NativeMemory.Free((void*)p); Current = null!; }

    internal static void Verify()
    {
        using var directory = new TemporaryWorkspace();
        string? previous = Environment.GetEnvironmentVariable(SettingsBindings.PathVariable);
        string path = Path.Combine(directory.Paths.Root, "drink.json");
        Environment.SetEnvironmentVariable(SettingsBindings.PathVariable, path);
        try
        {
            using (var f = new DrinkEligibilityFixture())
            {
                f.game.InstallDrinkEligibility(f.precompiled, 123);
                Check.Equal((nint)123, *(nint*)(f.function + SupportedBuild.ScriptVmEntry));
                Check.True(!f.game.CannotDrink(f.actor), "Exactly the configured cost must be affordable.");
                f.stamina = 2; Check.True(f.game.CannotDrink(f.actor), "Insufficient stamina was allowed.");
                f.cost = 1; Check.True(!f.game.CannotDrink(f.actor), "Affordability hardcoded the old threshold.");
                f.stamina = 0; f.cost = 0; Check.True(!f.game.CannotDrink(f.actor), "Free drink rejected.");
                fixed (char* key = SettingsBindings.FatigueKey)
                fixed (char* zero = "0")
                {
                    GameString value = new() { Data = zero, Length = 2, Capacity = 2 };
                    f.game.SetSetting(key, &value);
                }
                f.cost = 3; Check.True(!f.game.CannotDrink(f.actor), "OFF must allow a zero-stamina owner.");
                f.ownerMissing = true; Check.True(f.game.CannotDrink(f.actor), "OFF bypassed the missing-owner check.");
                f.ownerMissing = false; f.status = 1; Check.True(f.game.CannotDrink(f.actor), "Failed owner dispatch enabled drinking.");
                Check.Equal((nint)999, *(nint*)f.world);
                Check.Equal(7, f.releases);
                Check.True(f.logs.Any(x => x.Contains("eligibility failed")), "Missing owner-dispatch failure diagnostic.");
            }
            foreach (string fault in new[] { "module", "method", "arguments", "native", "parameter", "raw" })
            {
                using var f = new DrinkEligibilityFixture { foreignModule = fault == "module", missingMethod = fault == "method" };
                if (fault == "arguments") *(int*)(f.function + SupportedBuild.ScriptArgumentWords) = 1;
                if (fault == "native") *(nint*)(f.function + SupportedBuild.ScriptVmEntry) = 456;
                if (fault == "parameter") *(nint*)(f.function + SupportedBuild.ScriptParameterEntry) = 456;
                if (fault == "raw") *(nint*)(f.function + SupportedBuild.ScriptRawEntry) = 456;
                f.game.InstallDrinkEligibility(f.precompiled, 123);
                Check.True(*(nint*)(f.function + SupportedBuild.ScriptVmEntry) != 123, "Unsupported drink ABI was overwritten.");
                Check.True(f.logs.Count > 0, "Missing installation failure diagnostic.");
            }
        }
        finally { Environment.SetEnvironmentVariable(SettingsBindings.PathVariable, previous); }
    }

    [UnmanagedCallersOnly] private static nint GetModule(nint engine, byte* name, int flag) => Module;
    [UnmanagedCallersOnly] private static nint GetEngine(nint receiver) => Current.foreignModule ? 999 : Engine;
    [UnmanagedCallersOnly] private static nint FindType(nint module, byte* name) => Type;
    [UnmanagedCallersOnly] private static nint TypeName(nint receiver) => Current.Text("A_BP_Drink");
    [UnmanagedCallersOnly] private static nint NativeClass(nint type) => Current.nativeClass;
    [UnmanagedCallersOnly] private static nint FunctionName(nint receiver) => Current.Text("GetIsDrunk_Implementation");
    [UnmanagedCallersOnly] private static nint Method(nint type, byte* name) => Current.missingMethod ? 0 :
        Marshal.PtrToStringUTF8((nint)name) == "GetOwnerPlayerData" ? Current.getter : Current.function;
    [UnmanagedCallersOnly] private static uint Parameters(nint function) => 0;
    [UnmanagedCallersOnly] private static int ReturnType(nint function, uint* flags) { if (flags != null) *flags = 0; return 1; }
    [UnmanagedCallersOnly] private static nint Acquire(NativeGame.ScriptContextScope* scope)
    { scope->Context = Current.context; scope->Nested = 1; return (nint)scope; }
    [UnmanagedCallersOnly] private static void Release(NativeGame.ScriptContextScope* scope) => Current.releases++;
    [UnmanagedCallersOnly] private static int Prepare(nint context, nint function) { Current.prepared = function; return 0; }
    [UnmanagedCallersOnly] private static int SetObject(nint context, nint actor) => actor == Current.actor ? 0 : -1;
    [UnmanagedCallersOnly] private static int Execute(nint context) => Current.prepared == Current.getter ? Current.status : -1;
    [UnmanagedCallersOnly] private static nint Result(nint context) => Current.ownerMissing ? 0 : 1234;
    [UnmanagedCallersOnly] private static nint Female(int role) => role == 10001 ? Current.row : 0;
    [UnmanagedCallersOnly] private static void Read(GameString* role, int* current, int* maximum)
    { *current = Current.stamina; *maximum = 20; }
    [UnmanagedCallersOnly] private static int Config(char* key, int fallback) => Current.cost;
    [UnmanagedCallersOnly] private static void Log(char* text) => Current.logs.Add(new string(text));
}
