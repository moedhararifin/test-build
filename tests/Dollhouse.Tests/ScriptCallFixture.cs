using System.Runtime.InteropServices;
using Dollhouse.Gameplay;
using SupportedBuild = Dollhouse.Gameplay.SupportedBuild;

namespace Dollhouse.Tests;

// Models the loaded-method ABI and failures. Never loads the game or substitutes a script compiler.
internal sealed unsafe class ScriptCallFixture : IDisposable
{
    private const nint Context = 0x10000, Engine = 0x11000, RoomModule = 0x12000, PerformerModule = 0x13000;
    private const nint RoomType = 0x14000, PerformerType = 0x15000, Function = 0x16000;
    private const nint AnimModule = 0x17000, AnimType = 0x18000, ArrayType = 0x19000, AnimBaseType = 0x20000;
    private static ScriptCallFixture Current = null!;
    private readonly List<nint> allocations = [];
    private readonly Dictionary<string, nint> strings = [];
    private readonly List<string> logs = [];
    private readonly nint worldContext, activeWorld, modeClass, roomClass, performerClass, otherClass, mode;
    private nint room, performer;
    private nint animationClass, animation, actorClass, actor, mesh, preparedFunction;
    private const nint CharacterType = 0x21000, CharacterModule = 0x22000, BodyGetter = 0x23000;
    private bool enableGrowth, missingGetter, badGetterReturn, nullMesh;
    private nint meshClass;
    private nint baseCharacterClass;
    private const nint BaseCharacterModule = 0x25000, BaseCharacterType = 0x26000;
    private const nint MeshType = 0x24000;
    private double growthTarget = .8;
    private int frameCalls;
    private readonly Dictionary<ulong, nint> weakObjects = [];
    private readonly Dictionary<nint, ulong> weakKeys = [];
    private nint lastWorld, receiver;
    private bool missingMode, missingRoom, missingModule, missingType, foreignEngine;
    private bool missingProperty, wrongPropertyType, badOffset, referenceProperty, compositeProperty;
    private bool missingMethod, badSignature, wrongMethodOwner;
    private int prepareStatus, receiverStatus, executeStatus;
    internal bool MissingContext;
    internal byte Nested = 1;
    internal int Executions, Argument, Acquisitions, Disposals;
    internal Action? OnExecute;

    private nint Allocate(int bytes)
    {
        nint result = (nint)NativeMemory.AllocZeroed((nuint)bytes); allocations.Add(result); return result;
    }
    private nint NewClass()
    {
        nint result = Allocate(0x40), chain = Allocate(8);
        *(nint*)(result + 0x30) = chain; *(nint*)chain = result + 0x30;
        return result;
    }
    private nint NewObject(nint type, int size)
    { nint result = Allocate(size); *(nint*)(result + 0x10) = type; return result; }
    private nint DerivedClass(nint parent)
    {
        int depth = *(int*)(parent + 0x38) + 1;
        nint result = Allocate(0x40), chain = Allocate((depth + 1) * 8);
        *(nint*)(result + 0x30) = chain; *(int*)(result + 0x38) = depth;
        for (int i = 0; i < depth; i++) ((nint*)chain)[i] = ((nint*)(*(nint*)(parent + 0x30)))[i];
        ((nint*)chain)[depth] = result + 0x30;
        return result;
    }
    private void ReplaceRoom()
    {
        room = NewObject(roomClass, 0x100); performer = NewObject(performerClass, 0x100);
        *(nint*)(room + 0x60) = performer;
        if (enableGrowth)
        {
            animationClass = NewClass(); animation = NewObject(animationClass, 0x80);
            baseCharacterClass = NewClass(); actorClass = DerivedClass(baseCharacterClass);
            actor = NewObject(DerivedClass(actorClass), 0x80); meshClass = NewClass(); mesh = NewObject(meshClass, 0x40);
            nint items = Allocate(8); *(nint*)items = actor;
            *(int*)(performer + 0x40) = 42;
            *(nint*)(performer + 0x48) = items;
            *(int*)(performer + 0x50) = *(int*)(performer + 0x54) = 1;
        }
    }
    private void EnableGrowth() { enableGrowth = true; ReplaceRoom(); }
    private double Belly { get => *(double*)(animation + 0x40); set => *(double*)(animation + 0x40) = value; }
    internal ScriptCallFixture()
    {
        Current = this;
        worldContext = Allocate(8); activeWorld = Allocate(8);
        *(nint*)worldContext = 0xdead; *(nint*)activeWorld = 0x7000;
        modeClass = NewClass(); roomClass = NewClass(); performerClass = NewClass(); otherClass = NewClass();
        mode = NewObject(modeClass, 0x40); ReplaceRoom();
    }
    public void Dispose()
    {
        foreach (nint value in strings.Values) Marshal.FreeCoTaskMem(value);
        foreach (nint value in allocations) NativeMemory.Free((void*)value);
        Current = null!;
    }
    private nint Text(string value)
    {
        if (!strings.TryGetValue(value, out nint result)) strings.Add(value, result = Marshal.StringToCoTaskMemUTF8(value));
        return result;
    }
    private NativeGame Game() => new(0, rva => rva switch
    {
        SupportedBuild.CurrentWorld => activeWorld,
        SupportedBuild.GameplayLog => (nint)(delegate* unmanaged<char*, void>)&Log,
        _ => Resolve(rva)
    });
    internal nint Resolve(int rva) => rva switch
    {
        SupportedBuild.GetGameMode => (nint)(delegate* unmanaged<nint, nint>)&GetGameMode,
        SupportedBuild.RoomGameModeClass => (nint)(delegate* unmanaged<nint>)&GetModeClass,
        SupportedBuild.GetRoomManager => (nint)(delegate* unmanaged<nint, int, nint*, byte>)&GetRoom,
        SupportedBuild.ScriptScopeCreate => (nint)(delegate* unmanaged<NativeGame.ScriptContextScope*, nint>)&Acquire,
        SupportedBuild.ScriptScopeRelease => (nint)(delegate* unmanaged<NativeGame.ScriptContextScope*, void>)&ReleaseScope,
        SupportedBuild.ScriptGetEngine => (nint)(delegate* unmanaged<nint, nint>)&GetEngine,
        SupportedBuild.ScriptGetModule => (nint)(delegate* unmanaged<nint, byte*, int, nint>)&GetModule,
        SupportedBuild.ScriptModuleEngine => (nint)(delegate* unmanaged<nint, nint>)&GetModuleEngine,
        SupportedBuild.ScriptModuleFindType => (nint)(delegate* unmanaged<nint, byte*, nint>)&GetType,
        SupportedBuild.ScriptTypeName => (nint)(delegate* unmanaged<nint, nint>)&GetTypeName,
        SupportedBuild.ScriptTypeEngine => (nint)(delegate* unmanaged<nint, nint>)&GetTypeEngine,
        SupportedBuild.ScriptTypeNativeClass => (nint)(delegate* unmanaged<nint, nint>)&GetTypeClass,
        SupportedBuild.ScriptTypeSize => (nint)(delegate* unmanaged<nint, uint>)&GetTypeSize,
        SupportedBuild.ScriptTypeId => (nint)(delegate* unmanaged<nint, int>)&GetTypeId,
        SupportedBuild.ScriptTypeById => (nint)(delegate* unmanaged<nint, int, nint>)&GetTypeById,
        SupportedBuild.ScriptSubtypeId => (nint)(delegate* unmanaged<nint, uint, int>)&SubtypeId,
        SupportedBuild.ScriptSubtype => (nint)(delegate* unmanaged<nint, uint, nint>)&Subtype,
        SupportedBuild.AssignWeakObject => (nint)(delegate* unmanaged<ulong*, nint*, void>)&AssignWeak,
        SupportedBuild.GetWeakObject => (nint)(delegate* unmanaged<ulong*, nint>)&GetWeak,
        SupportedBuild.FrameTickerTick => (nint)(delegate* unmanaged<nint, float, void>)&FrameTick,
        SupportedBuild.ScriptPropertyCount => (nint)(delegate* unmanaged<nint, uint>)&PropertyCount,
        SupportedBuild.ScriptProperty => (nint)(delegate* unmanaged<nint, uint, nint*, int*, byte*, byte*, int*, byte*, uint*, int*, byte*, int>)&GetProperty,
        SupportedBuild.ScriptMethodByName => (nint)(delegate* unmanaged<nint, byte*, nint>)&GetMethod,
        SupportedBuild.ScriptFunctionName => (nint)(delegate* unmanaged<nint, nint>)&GetMethodName,
        SupportedBuild.ScriptFunctionObjectName => (nint)(delegate* unmanaged<nint, nint>)&GetMethodOwner,
        SupportedBuild.ScriptParameterCount => (nint)(delegate* unmanaged<nint, uint>)&GetParameterCount,
        SupportedBuild.ScriptReturnType => (nint)(delegate* unmanaged<nint, uint*, int>)&GetReturnType,
        SupportedBuild.ScriptPrepare => (nint)(delegate* unmanaged<nint, nint, int>)&Prepare,
        SupportedBuild.ScriptSetObject => (nint)(delegate* unmanaged<nint, nint, int>)&SetObject,
        SupportedBuild.ScriptReturnObject => (nint)(delegate* unmanaged<nint, nint>)&ReturnObject,
        SupportedBuild.MeshAnimInstance => (nint)(delegate* unmanaged<nint, nint>)&GetAnimation,
        SupportedBuild.ScriptExecute => (nint)(delegate* unmanaged<nint, int>)&Execute,
        SupportedBuild.ScriptExceptionString => (nint)(delegate* unmanaged<nint, nint>)&ExceptionString,
        SupportedBuild.ScriptExceptionLine => (nint)(delegate* unmanaged<nint, int*, nint*, int>)&ExceptionLine,
        SupportedBuild.ScriptWorldContext => worldContext,
        _ => 0
    };
    private void Invoke(NativeGame game)
    {
        game.RefreshExperimentBulges(7);
        Check.Equal((nint)0xdead, *(nint*)worldContext);
        Check.Equal(MissingContext ? 0 : Acquisitions, Disposals);
    }
    private void Logged(string fragment) => Check.True(logs.Any(x => x.Contains(fragment, StringComparison.Ordinal)), "Missing diagnostic: " + fragment);
    internal static void Verify()
    {
        Check.Equal(0x10, sizeof(NativeGame.ScriptContextScope));
        Check.Equal(8, Marshal.OffsetOf<NativeGame.ScriptContextScope>(nameof(NativeGame.ScriptContextScope.Nested)).ToInt32());
        using (var f = new ScriptCallFixture())
        {
            var game = f.Game(); f.Invoke(game);
            Check.Equal(1, f.Executions); Check.Equal(7, f.Argument); Check.Equal(f.performer, f.receiver);
            nint previous = f.receiver; f.ReplaceRoom(); *(nint*)f.activeWorld = 0x8000; f.Nested = 0; f.Invoke(game);
            Check.Equal(2, f.Executions); Check.True(previous != f.receiver, "Retained stale performer."); Check.Equal((nint)0x8000, f.lastWorld);
        }
        using (var f = new ScriptCallFixture())
        {
            *(nint*)(f.mode + 0x10) = f.DerivedClass(f.modeClass);
            *(nint*)(f.room + 0x10) = f.DerivedClass(f.roomClass);
            *(nint*)(f.performer + 0x10) = f.DerivedClass(f.performerClass);
            f.Invoke(f.Game()); Check.Equal(1, f.Executions);
        }
        void Reject(Action<ScriptCallFixture> configure, string? diagnostic)
        {
            using var f = new ScriptCallFixture(); configure(f); f.Invoke(f.Game());
            Check.Equal(0, f.Executions); if (diagnostic != null) f.Logged(diagnostic);
        }
        Reject(f => f.missingMode = true, null); // Normal outside a room, not a warning.
        Reject(f => f.missingRoom = true, null);
        Reject(f => f.MissingContext = true, "context failed");
        Reject(f => f.missingModule = true, "module absent");
        Reject(f => f.missingType = true, "type unavailable");
        Reject(f => f.foreignEngine = true, "type unavailable");
        Reject(f => { *(nint*)(f.room + 0x10) = f.otherClass; }, "class does not match");
        Reject(f => f.missingProperty = true, "property missing");
        Reject(f => f.wrongPropertyType = true, "HPerformer type rejected");
        Reject(f => f.badOffset = true, "property rejected");
        Reject(f => f.referenceProperty = true, "property rejected");
        Reject(f => f.compositeProperty = true, "property rejected");
        Reject(f => { *(nint*)(f.room + 0x60) = 0; }, "performer absent or wrong class");
        Reject(f => { *(nint*)(f.performer + 0x10) = f.otherClass; }, "performer absent or wrong class");
        Reject(f => f.missingMethod = true, "method absent");
        Reject(f => f.badSignature = true, "signature rejected");
        Reject(f => f.wrongMethodOwner = true, "signature rejected");
        Reject(f => f.prepareStatus = -2, "preparation failed");
        Reject(f => f.receiverStatus = -5, "receiver failed");
        using (var f = new ScriptCallFixture { executeStatus = 3 })
        { f.Invoke(f.Game()); f.Logged("script exception: test:17:2: synthetic error"); Check.Equal(1, f.Executions); }
        using (var f = new ScriptCallFixture())
        { *(nint*)f.activeWorld = 0; f.Invoke(f.Game()); Check.Equal(0, f.Acquisitions); }
        VerifyGrowth();
    }
    private static void VerifyGrowth()
    {
        void Near(double expected, double actual) => Check.True(Math.Abs(expected - actual) < 1e-9, $"Expected belly {expected}; got {actual}.");
        using (var f = new ScriptCallFixture())
        {
            f.EnableGrowth(); var game = f.Game(); f.Invoke(game);
            Near(0, f.Belly); // The native target was captured and the old visual restored in the same callback.
            game.TickCumflationVisuals(0x9999, .5f); Near(.2, f.Belly);
            game.TickCumflationVisuals(0x9999, .5f); Near(.4, f.Belly);
            f.growthTarget = 1; f.Invoke(game); Near(.4, f.Belly);
            int logCount = f.logs.Count;
            game.TickCumflationVisuals(0x9999, 1); Near(.7, f.Belly);
            game.TickCumflationVisuals(0x9999, 1); Near(1, f.Belly);
            game.TickCumflationVisuals(0x9999, 1); Near(1, f.Belly); Check.Equal(5, f.frameCalls);
            Check.Equal(logCount, f.logs.Count); // Transition progress/completion is silent.
        }
        foreach (string rejection in new[] { "missing", "return", "mesh" })
        {
            using var f = new ScriptCallFixture(); f.EnableGrowth();
            f.missingGetter = rejection == "missing"; f.badGetterReturn = rejection == "return"; f.nullMesh = rejection == "mesh";
            var game = f.Game(); f.Invoke(game); Near(.8, f.Belly);
            game.TickCumflationVisuals(0x9999, 1); Near(.8, f.Belly);
            f.Logged(rejection == "missing" ? "body getter unavailable" : rejection == "return" ? "body getter signature rejected" : "body mesh absent");
        }
        using (var f = new ScriptCallFixture())
        {
            f.EnableGrowth(); *(nint*)(f.actor + 0x10) = f.baseCharacterClass;
            var game = f.Game(); f.Invoke(game); Near(.8, f.Belly);
            // Native UpdateBulge also casts specifically to female; other actors are silently skipped.
        }
        foreach (string cancel in new[] { "world", "card", "expired", "native", "drain" })
        {
            using var f = new ScriptCallFixture(); f.EnableGrowth(); var game = f.Game(); f.Invoke(game);
            game.TickCumflationVisuals(0x9999, .5f); Near(.2, f.Belly);
            switch (cancel)
            {
                case "world": *(nint*)f.activeWorld = 0x8000; break;
                case "card": *(int*)(f.performer + 0x40) = 43; break;
                case "expired": f.weakObjects.Remove(f.weakKeys[f.animation]); break;
                case "native": f.Belly = .6; break;
                case "drain": game.CancelExperimentBulgeGrowth(); break;
            }
            double before = f.Belly; game.TickCumflationVisuals(0x9999, 1); Near(before, f.Belly);
        }
        using (var f = new ScriptCallFixture())
        {
            f.EnableGrowth(); f.Belly = .8; f.growthTarget = .2; var game = f.Game(); f.Invoke(game);
            game.TickCumflationVisuals(0x9999, 1); Near(.2, f.Belly); // Native decreases are not tweened.
        }
    }
    [UnmanagedCallersOnly] private static nint GetGameMode(nint world) { Current.lastWorld = world; return Current.missingMode ? 0 : Current.mode; }
    [UnmanagedCallersOnly] private static nint GetModeClass() => Current.modeClass;
    [UnmanagedCallersOnly] private static byte GetRoom(nint mode, int player, nint* room)
    { Check.Equal(Current.mode, mode); Current.Argument = player; *room = Current.missingRoom ? 0 : Current.room; return (byte)(*room == 0 ? 0 : 1); }
    [UnmanagedCallersOnly] private static nint Acquire(NativeGame.ScriptContextScope* scope)
    { Current.Acquisitions++; scope->Context = Current.MissingContext ? 0 : Context; scope->Nested = Current.Nested; return (nint)scope; }
    [UnmanagedCallersOnly] private static void ReleaseScope(NativeGame.ScriptContextScope* scope)
    {
        Check.Equal(Context, scope->Context); Check.Equal(Current.Nested, scope->Nested);
        Check.Equal(Current.lastWorld, *(nint*)Current.worldContext); Current.Disposals++;
    }
    [UnmanagedCallersOnly] private static nint GetEngine(nint context) { Check.Equal(Context, context); return Engine; }
    [UnmanagedCallersOnly] private static nint GetModule(nint engine, byte* name, int flags)
    {
        Check.Equal(Engine, engine); Check.Equal(0, flags); // No creation or compiler fallback is permitted.
        if (Current.missingModule) return 0;
        return Marshal.PtrToStringUTF8((nint)name) switch { "Core.BP.RoomManager" => RoomModule, "Core.BP.H.Common.HPerformer" => PerformerModule,
            "Actor.Base.BaseAnimCharacter" => BaseCharacterModule,
            "Actor.Character.Base.CharacterFemale" => CharacterModule, "AMBP.Character.AMBP_Character" => AnimModule, "AMBP.ALS.ALS_FemaleAnimInstance" => 0, "AMBP.Monster.AMBP_Monster" => 0,
            _ => throw new InvalidOperationException("Unexpected module.") };
    }
    [UnmanagedCallersOnly] private static nint GetModuleEngine(nint module) => Engine;
    [UnmanagedCallersOnly] private static nint GetType(nint module, byte* name)
    {
        string expected = module == BaseCharacterModule ? "A_BaseAnimCharacter" : module == CharacterModule ? "A_CharacterFemale" : module == RoomModule ? "A_RoomManager" : module == AnimModule ? "U_AMBP_Character" : "A_HPerformer";
        Check.Equal(expected, Marshal.PtrToStringUTF8((nint)name)!);
        return Current.missingType ? 0 : module == BaseCharacterModule ? BaseCharacterType : module == CharacterModule ? CharacterType : module == RoomModule ? RoomType : module == AnimModule ? AnimType : PerformerType;
    }
    [UnmanagedCallersOnly] private static nint GetTypeName(nint type) => Current.Text(type == BaseCharacterType ? "A_BaseAnimCharacter" : type == MeshType ? "USkeletalMeshComponent" : type == RoomType ? "A_RoomManager" : type == CharacterType ? "A_CharacterFemale" : type == AnimBaseType ? "A_BaseAnimCharacter" : type == ArrayType ? "TArray" : type == AnimType ? "U_AMBP_Character" : "A_HPerformer");
    [UnmanagedCallersOnly] private static nint GetTypeEngine(nint type) => Current.foreignEngine ? 0xdead : Engine;
    [UnmanagedCallersOnly] private static nint GetTypeClass(nint type) => type == BaseCharacterType ? Current.baseCharacterClass : type == MeshType ? Current.meshClass : type == CharacterType ? Current.actorClass : type == RoomType ? Current.roomClass : type == AnimType ? Current.animationClass : Current.performerClass;
    [UnmanagedCallersOnly] private static uint GetTypeSize(nint type) => 0x100;
    [UnmanagedCallersOnly] private static int GetTypeId(nint type) { Check.Equal(PerformerType, type); return 0x08000002; }
    [UnmanagedCallersOnly] private static uint PropertyCount(nint type) => type == RoomType ? 2U : !Current.enableGrowth ? 0U : type == AnimType ? 1U : 2U;
    [UnmanagedCallersOnly] private static nint GetTypeById(nint engine, int id)
    {
        Check.Equal(Engine, engine);
        return (id & 0x1fffffff) switch { 0x08000051 => MeshType, 0x08000052 => AnimType, 0x04000044 => ArrayType,
            _ => throw new InvalidOperationException("Unexpected script type ID.") };
    }
    [UnmanagedCallersOnly] private static int SubtypeId(nint type, uint index) { Check.Equal(ArrayType, type); Check.Equal(0U, index); return 0x44000045; }
    [UnmanagedCallersOnly] private static nint Subtype(nint type, uint index) { Check.Equal(ArrayType, type); Check.Equal(0U, index); return AnimBaseType; }
    [UnmanagedCallersOnly] private static int GetProperty(nint type, uint index, nint* name, int* typeId, byte* privacy,
        byte* protection, int* offset, byte* reference, uint* access, int* composite, byte* indirect)
    {
        Check.True(index < 2, "Out-of-range property index.");
        if (type != RoomType)
        {
            *name = Current.Text(type == AnimType ? "BlendForBulge" : index == 0 ? "CardID" : "AnimInstanceList");
            *typeId = type == AnimType ? 11 : index == 0 ? 4 : 0x04000044;
            *offset = type == AnimType || index == 0 ? 0x40 : 0x48;
            *reference = *indirect = 0; *composite = 0; return 0;
        }
        *name = Current.Text(index == 1 && !Current.missingProperty ? "HPerformer" : "Other");
        *typeId = Current.wrongPropertyType ? 1 : 0x48000002;
        *offset = Current.badOffset ? 0x100 : 0x60;
        *reference = (byte)(Current.referenceProperty ? 1 : 0); *composite = Current.compositeProperty ? 8 : 0; *indirect = 0;
        return 0;
    }
    [UnmanagedCallersOnly] private static nint GetMethod(nint type, byte* name)
    { if (type == BaseCharacterType) return 0; if (type == CharacterType) { Check.Equal("GetMainBodyPart", Marshal.PtrToStringUTF8((nint)name)!); return Current.missingGetter ? 0 : BodyGetter; } Check.Equal(PerformerType, type); Check.Equal("UpdateBulge", Marshal.PtrToStringUTF8((nint)name)!); return Current.missingMethod ? 0 : Function; }
    [UnmanagedCallersOnly] private static nint GetMethodName(nint function) { return Current.Text(function == BodyGetter ? "GetMainBodyPart" : "UpdateBulge"); }
    [UnmanagedCallersOnly] private static nint GetMethodOwner(nint function) => Current.Text(function == BodyGetter ? "A_CharacterFemale" : Current.wrongMethodOwner ? "AvatarPawn" : "A_HPerformer");
    [UnmanagedCallersOnly] private static uint GetParameterCount(nint function) => Current.badSignature ? 1U : 0;
    [UnmanagedCallersOnly] private static int GetReturnType(nint function, uint* modifiers) => function == BodyGetter ? Current.badGetterReturn ? 0x08000052 : 0x08000051 : 0;
    [UnmanagedCallersOnly] private static int Prepare(nint context, nint function)
    { Check.Equal(Context, context); Current.preparedFunction = function; return Current.prepareStatus; }
    [UnmanagedCallersOnly] private static int SetObject(nint context, nint instance)
    { Check.Equal(Context, context); Check.Equal(Current.preparedFunction == BodyGetter ? Current.actor : Current.performer, instance); Current.receiver = instance; return Current.receiverStatus; }
    [UnmanagedCallersOnly] private static int Execute(nint context)
    {
        Check.Equal(Context, context); if (Current.preparedFunction == BodyGetter) return 0; Check.Equal(Current.performer, Current.receiver); Current.Executions++;
        if (Current.enableGrowth) Current.Belly = Current.growthTarget;
        Current.OnExecute?.Invoke(); return Current.executeStatus;
    }
    [UnmanagedCallersOnly] private static nint ReturnObject(nint context) => Current.nullMesh ? 0 : Current.mesh;
    [UnmanagedCallersOnly] private static nint GetAnimation(nint mesh) { Check.Equal(Current.mesh, mesh); return Current.animation; }
    [UnmanagedCallersOnly] private static void AssignWeak(ulong* weak, nint* instance)
    {
        if (!Current.weakKeys.TryGetValue(*instance, out ulong key))
        { key = 0x100000000UL + (uint)Current.weakKeys.Count + 1; Current.weakKeys.Add(*instance, key); Current.weakObjects.Add(key, *instance); }
        *weak = key;
    }
    [UnmanagedCallersOnly] private static nint GetWeak(ulong* weak) => Current.weakObjects.GetValueOrDefault(*weak);
    [UnmanagedCallersOnly] private static void FrameTick(nint ticker, float seconds)
    { Check.Equal((nint)0x9999, ticker); Current.frameCalls++; }
    [UnmanagedCallersOnly] private static nint ExceptionString(nint context) => Current.Text("synthetic error");
    [UnmanagedCallersOnly] private static int ExceptionLine(nint context, int* column, nint* section)
    { *column = 2; *section = Current.Text("test"); return 17; }
    [UnmanagedCallersOnly] private static void Log(char* text) => Current.logs.Add(new string(text));
}
