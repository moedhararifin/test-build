using System.Runtime.InteropServices;
using Dollhouse.Gameplay;

namespace Dollhouse.Tests;

// Realistic native boundaries, not a game run. Models both initial startup and a UI subscriber
// left registered by character switching. The copied delegate may be compacted by Broadcast.
internal sealed unsafe class DrainAlertFixture : IDisposable
{
    private const nint Context = 1, Engine = 2, UiModule = 3, LanguageModule = 4, UiType = 5, StringType = 6;
    private const nint Clear = 10, Progress = 11, Button = 12, Language = 13, Show = 14, SetText = 15, Subsystem = 20;
    private const nint MenuModule = 7, MenuType = 8, TipType = 9;
    private static DrainAlertFixture Current = null!;
    private readonly List<nint> allocations = [];
    private readonly Dictionary<string, nint> strings = [];
    private readonly HashSet<nint> gameBuffers = [];
    private readonly List<string> logs = [];
    private readonly nint activeWorld, worldContext, uiClass, widget, other, originalDelegate, result;
    private readonly nint menu, menuClass, tip;
    private int womb, gi, writes, topCalls, rightCalls, clearCalls, progressCalls, buttonCalls, nativeCallbacks, originalBroadcasts, scopes, releases;
    private nint prepared;
    private string languageKey = "", topText = "", rightNumber = "";
    private bool unsupportedTemplate, missingModule, missingResetMethod, languageException, shrinkCopy, failRead;
    private bool missingMenu, missingTip, wrongTipParameter;
    private int shows;
    private uint languageModifiers = 7; // User log: actual GetLanguage const FString& metadata.
    private uint valueModifiers = 4; // Saved method signatures: const double/bool by value.
    private bool wrongProgressType;
    [StructLayout(LayoutKind.Sequential)] private struct Binding { internal ulong Weak, Name; }
    [StructLayout(LayoutKind.Sequential)] private struct Delegate { internal Binding* Data; internal int Count, Capacity; }
    private nint Allocate(int bytes) { nint p = (nint)NativeMemory.AllocZeroed((nuint)bytes); allocations.Add(p); return p; }
    private nint Text(string text)
    {
        if (!strings.TryGetValue(text, out nint p)) strings.Add(text, p = Marshal.StringToCoTaskMemUTF8(text));
        return p;
    }
    private static string Read(GameString* text) => text == null || text->Data == null ? "" : new(text->Data, 0, Math.Max(0, text->Length - 1));
    private void Return(string text)
    {
        char* data = (char*)Allocate((text.Length + 1) * 2);
        text.AsSpan().CopyTo(new Span<char>(data, text.Length));
        *(GameString*)result = new() { Data = data, Length = text.Length + 1, Capacity = text.Length + 1 };
    }
    private DrainAlertFixture(bool subscribed)
    {
        Current = this; activeWorld = Allocate(8); worldContext = Allocate(8); result = Allocate(16);
        menu = Allocate(0x100);
        menuClass = Allocate(0x40); nint msgChain = Allocate(8);
        *(nint*)(menuClass + 0x30) = msgChain; *(nint*)msgChain = menuClass + 0x30;
        *(nint*)(menu + 0x10) = menuClass; *(byte*)(menu + 0x40) = 1;
        *(nint*)activeWorld = 100; *(nint*)worldContext = 200;
        uiClass = Allocate(0x40); nint chain = Allocate(8); *(nint*)(uiClass + 0x30) = chain; *(nint*)chain = uiClass + 0x30;
        widget = Allocate(0x100); *(nint*)(widget + 0x10) = uiClass;
        tip = Allocate(0x30); *(nint*)(tip + 0x10) = menuClass;
        nint otherClass = Allocate(0x40), otherChain = Allocate(8);
        *(nint*)(otherClass + 0x30) = otherChain; *(nint*)otherChain = otherClass + 0x30;
        other = Allocate(0x30); *(nint*)(other + 0x10) = otherClass;
        originalDelegate = Allocate(16); var d = (Delegate*)originalDelegate;
        d->Data = (Binding*)Allocate(48); d->Capacity = 3; d->Count = subscribed ? 3 : 2;
        d->Data[0] = new() { Weak = 2, Name = 1000 }; d->Data[1] = new() { Weak = 3, Name = 1001 };
        if (subscribed) d->Data[2] = new() { Weak = 1, Name = 1002 };
    }
    public void Dispose()
    {
        Check.Equal(0, gameBuffers.Count); Check.Equal(scopes, releases); Check.Equal((nint)200, *(nint*)worldContext);
        foreach (var p in allocations) NativeMemory.Free((void*)p);
        foreach (var p in strings.Values) Marshal.FreeCoTaskMem(p);
        Current = null!;
    }
    private NativeGame Game() => new(0, rva => rva switch
    {
        SupportedBuild.CurrentWorld => activeWorld, SupportedBuild.ScriptWorldContext => worldContext,
        SupportedBuild.ReadRoleAttribute when !failRead => (nint)(delegate* unmanaged<GameString*, int, int>)&ReadAttribute,
        SupportedBuild.WriteRoleAttribute => (nint)(delegate* unmanaged<GameString*, int, int, void>)&WriteAttribute,
        SupportedBuild.GameplayLog => (nint)(delegate* unmanaged<char*, void>)&Log,
        SupportedBuild.ScriptScopeCreate => (nint)(delegate* unmanaged<NativeGame.ScriptContextScope*, nint>)&CreateScope,
        SupportedBuild.ScriptScopeRelease => (nint)(delegate* unmanaged<NativeGame.ScriptContextScope*, void>)&ReleaseScope,
        SupportedBuild.ScriptGetEngine => (nint)(delegate* unmanaged<nint, nint>)&GetEngine,
        SupportedBuild.ScriptGetModule => (nint)(delegate* unmanaged<nint, byte*, int, nint>)&GetModule,
        SupportedBuild.ScriptModuleEngine => (nint)(delegate* unmanaged<nint, nint>)&GetEngine,
        SupportedBuild.ScriptModuleFindType => (nint)(delegate* unmanaged<nint, byte*, nint>)&GetType,
        SupportedBuild.ScriptTypeEngine => (nint)(delegate* unmanaged<nint, nint>)&GetEngine,
        SupportedBuild.ScriptTypeName => (nint)(delegate* unmanaged<nint, nint>)&TypeName,
        SupportedBuild.ScriptTypeNativeClass => (nint)(delegate* unmanaged<nint, nint>)&TypeClass,
        SupportedBuild.ScriptPropertyCount => (nint)(delegate* unmanaged<nint, uint>)&PropertyCount,
        SupportedBuild.ScriptTypeSize => (nint)(delegate* unmanaged<nint, uint>)&TypeSize,
        SupportedBuild.ScriptProperty => (nint)(delegate* unmanaged<nint, uint, nint*, int*, byte*, byte*, int*, byte*, uint*, int*, byte*, int>)&Property,
        SupportedBuild.AllWidgetsOfClass => (nint)(delegate* unmanaged<nint, Delegate*, nint*, byte, void>)&AllWidgets,
        SupportedBuild.WidgetVisible => (nint)(delegate* unmanaged<nint, byte>)&WidgetVisible,
        SupportedBuild.ScriptModuleFunctionCount => (nint)(delegate* unmanaged<nint, uint>)&FunctionCount,
        SupportedBuild.ScriptModuleFunction => (nint)(delegate* unmanaged<nint, uint, nint>)&ModuleFunction,
        SupportedBuild.ScriptFunctionName => (nint)(delegate* unmanaged<nint, nint>)&FunctionName,
        SupportedBuild.ScriptFunctionNamespace => (nint)(delegate* unmanaged<nint, nint>)&FunctionNamespace,
        SupportedBuild.ScriptFunctionObjectName => (nint)(delegate* unmanaged<nint, nint>)&FunctionOwner,
        SupportedBuild.ScriptParameterCount => (nint)(delegate* unmanaged<nint, uint>)&ParameterCount,
        SupportedBuild.ScriptParameter => (nint)(delegate* unmanaged<nint, uint, int*, uint*, nint*, nint*, int>)&Parameter,
        SupportedBuild.ScriptReturnType => (nint)(delegate* unmanaged<nint, uint*, int>)&ReturnType,
        SupportedBuild.ScriptTypeById => (nint)(delegate* unmanaged<nint, int, nint>)&TypeById,
        SupportedBuild.ScriptMethodByName => (nint)(delegate* unmanaged<nint, byte*, nint>)&Method,
        SupportedBuild.ScriptPrepare => (nint)(delegate* unmanaged<nint, nint, int>)&Prepare,
        SupportedBuild.ScriptSetObject => (nint)(delegate* unmanaged<nint, nint, int>)&SetObject,
        SupportedBuild.ScriptSetArgAddress => (nint)(delegate* unmanaged<nint, uint, nint, int>)&SetAddress,
        SupportedBuild.ScriptSetArgDouble => (nint)(delegate* unmanaged<nint, uint, double, int>)&SetDouble,
        SupportedBuild.ScriptSetArgByte => (nint)(delegate* unmanaged<nint, uint, byte, int>)&SetByte,
        SupportedBuild.ScriptExecute => (nint)(delegate* unmanaged<nint, int>)&Execute,
        SupportedBuild.ScriptReturnObject => (nint)(delegate* unmanaged<nint, nint>)&ReturnObject,
        SupportedBuild.ScriptExceptionString => (nint)(delegate* unmanaged<nint, nint>)&ExceptionString,
        SupportedBuild.ScriptExceptionLine => (nint)(delegate* unmanaged<nint, int*, nint*, int>)&ExceptionLine,
        SupportedBuild.Allocate => (nint)(delegate* unmanaged<nuint, uint, nint>)&GameAllocate,
        SupportedBuild.Free => (nint)(delegate* unmanaged<void*, void>)&GameFree,
        SupportedBuild.GetWeakObject => (nint)(delegate* unmanaged<ulong*, nint>)&GetWeak,
        SupportedBuild.Broadcast => (nint)(delegate* unmanaged<nint, GameString*, void>)&Broadcast,
        SupportedBuild.ShowRightMsg => (nint)(delegate* unmanaged<nint, byte, GameString*, GameString*, GameString*, GameString*, void>)&ShowRight,
        _ => throw new InvalidOperationException("Unexpected drain native API.")
    });
    private void Drain(NativeGame game, string role, int womb, int gi)
    {
        this.womb = womb; this.gi = gi;
        fixed (char* chars = role)
        {
            GameString value = new() { Data = chars, Length = role.Length + 1, Capacity = role.Length + 1 };
            byte[] before = new ReadOnlySpan<byte>(((Delegate*)originalDelegate)->Data, 48).ToArray();
            int count = ((Delegate*)originalDelegate)->Count;
            game.CaptureDrainAndWrite(&value, 73, 0); Check.Equal(0, this.gi); Check.Equal(womb, this.womb);
            this.womb = 0; // Original second write remains native.
            game.DispatchDrainCleared(originalDelegate, &value);
            Check.Equal(count, ((Delegate*)originalDelegate)->Count);
            Check.True(new ReadOnlySpan<byte>(((Delegate*)originalDelegate)->Data, 48).SequenceEqual(before), "Registered delegate was modified.");
            GameString empty = default;
            game.ShowDrainRightMsg(Subsystem, 40, &value, &empty, &empty, &empty);
        }
    }
    private void Logged(string text) => Check.True(logs.Any(x => x.Contains(text, StringComparison.Ordinal)), "Missing drain log: " + text);
    internal static void Verify()
    {
        Check.Equal("999mL", new DrainNotice("A", 0, 999).Volume);
        Check.Equal("1L", new DrainNotice("A", 800, 200).Volume);
        foreach (bool subscribed in new[] { false, true })
        {
            using var f = new DrainAlertFixture(subscribed) { shrinkCopy = true };
            var game = f.Game();
            // Fresh gameplay, then an open character menu, then closed menu with
            // its child subscriber deliberately still registered: the user's repro.
            *(byte*)(f.menu + 0x40) = 0;
            f.Drain(game, "A", 0, 883);
            Check.Equal(0, f.topCalls); Check.Equal("883mL", f.rightNumber);
            Check.Equal(1, f.rightCalls); Check.Equal(2, f.nativeCallbacks);
            game.NotifyVolumesCleared(0, null);
            if (!subscribed) continue;
            *(byte*)(f.menu + 0x40) = 1;
            f.Drain(game, "A", 0, 847);
            Check.Equal(1, f.shows); Check.Equal(1, f.topCalls);
            Check.Equal("Translated A expelled 847mL", f.topText);
            Check.Equal(1, f.rightCalls); // No red duplicate after successful native menu tip.
            game.NotifyVolumesCleared(0, null);
            *(byte*)(f.menu + 0x40) = 0;
            f.Drain(game, "B", 1000, 0);
            Check.Equal(1, f.topCalls); Check.Equal(2, f.rightCalls); Check.Equal("1L", f.rightNumber);
            Check.Equal(0, f.originalBroadcasts); Check.Equal(6, f.nativeCallbacks);
            Check.Equal(0, f.logs.Count); // Successful menu/gameplay routing is silent.
            game.NotifyVolumesCleared(0, null);
        }
        foreach (uint modifiers in new uint[] { 0, 4, 1, 2, 3, 5, 6, 7 })
        {
            using var f = new DrainAlertFixture(true) { valueModifiers = modifiers };
            var game = f.Game(); f.Drain(game, "A", 0, 847);
            bool accepted = modifiers is 0 or 4;
            Check.Equal(accepted ? 1 : 0, f.topCalls);
            Check.Equal(accepted ? 0 : 1, f.rightCalls);
            Check.Equal(accepted ? 0 : 1, f.originalBroadcasts);
            if (!accepted) f.Logged($"method=DelaySetFluidProgress, index=2, status=0, type=b, expectedType=b, modifiers={modifiers:x}");
            else Check.Equal(0, f.logs.Count);
            game.NotifyVolumesCleared(0, null);
        }
        foreach (uint modifiers in new uint[] { 5, 7, 0, 1, 2, 3, 6 })
        {
            using var f = new DrainAlertFixture(true) { languageModifiers = modifiers };
            var game = f.Game(); f.Drain(game, "A", 0, 794);
            bool accepted = modifiers is 5 or 7;
            Check.Equal(accepted ? 1 : 0, f.topCalls); Check.Equal(accepted ? 0 : 1, f.rightCalls);
            if (!accepted) f.Logged($"modifiers={modifiers:x}");
            else Check.Equal(0, f.logs.Count);
            game.NotifyVolumesCleared(0, null);
        }
        foreach (string failure in new[] { "template", "module", "method", "exception", "read", "tip", "tip-type", "progress-type", "menu" })
        {
            using var f = new DrainAlertFixture(true);
            f.unsupportedTemplate = failure == "template"; f.missingModule = failure == "module";
            f.missingResetMethod = failure == "method"; f.languageException = failure == "exception";
            f.failRead = failure == "read"; f.missingTip = failure == "tip";
            f.wrongTipParameter = failure == "tip-type"; f.wrongProgressType = failure == "progress-type";
            f.missingMenu = failure == "menu";
            var game = f.Game(); f.Drain(game, "A", 0, 730);
            Check.Equal(0, f.topCalls); Check.Equal(1, f.rightCalls); Check.Equal(1, f.writes);
            Check.Equal(f.failRead ? "" : "730mL", f.rightNumber);
            game.NotifyVolumesCleared(0, null);
        }
        using (var f = new DrainAlertFixture(true))
        {
            var game = f.Game(); f.Drain(game, "A", 0, 730); // Outer menu scope suppresses red.
            *(byte*)(f.menu + 0x40) = 0;
            f.Drain(game, "B", 1000, 0); Check.Equal("1L", f.rightNumber);
            game.NotifyVolumesCleared(0, null); // Restore outer scope, including suppression.
            GameString empty = default;
            game.ShowDrainRightMsg(Subsystem, 40, &empty, &empty, &empty, &empty);
            Check.Equal(1, f.rightCalls);
            game.ShowDrainRightMsg(Subsystem, 26, &empty, &empty, &empty, &empty);
            Check.Equal(2, f.rightCalls); // Other message kinds always forward.
            game.NotifyVolumesCleared(0, null);
            game.ShowDrainRightMsg(Subsystem, 40, &empty, &empty, &empty, &empty);
            Check.Equal(3, f.rightCalls); Check.Equal("", f.rightNumber); // No stale suppression/amount.
        }
    }
    [UnmanagedCallersOnly] private static int ReadAttribute(GameString* role, int key) => key == 74 ? Current.womb : Current.gi;
    [UnmanagedCallersOnly] private static void WriteAttribute(GameString* role, int key, int value) { Check.Equal(73, key); Current.gi = value; Current.writes++; }
    [UnmanagedCallersOnly] private static void Log(char* text) => Current.logs.Add(new string(text));
    [UnmanagedCallersOnly] private static nint CreateScope(NativeGame.ScriptContextScope* scope) { scope->Context = Context; Current.scopes++; return (nint)scope; }
    [UnmanagedCallersOnly] private static void ReleaseScope(NativeGame.ScriptContextScope* scope) { Check.Equal((nint)100, *(nint*)Current.worldContext); Current.releases++; }
    [UnmanagedCallersOnly] private static nint GetEngine(nint value) => Engine;
    [UnmanagedCallersOnly] private static nint GetModule(nint engine, byte* name, int flags)
    { Check.Equal(0, flags); return Current.missingModule ? 0 : Marshal.PtrToStringUTF8((nint)name) switch
        { "Core.GUI.Character.UICharacterState" => UiModule, "Core.GUI.Character.UICharacter" => MenuModule, _ => LanguageModule }; }
    [UnmanagedCallersOnly] private static nint GetType(nint module, byte* name)
    { Check.Equal(module == UiModule ? "U_UICharacterState" : "U_UICharacter", Marshal.PtrToStringUTF8((nint)name)!); return module == UiModule ? UiType : MenuType; }
    [UnmanagedCallersOnly] private static nint TypeName(nint type) => Current.Text(type == UiType ? "U_UICharacterState" : type == MenuType ? "U_UICharacter" : type == TipType ? "U_UI_TopTips" : "FString");
    [UnmanagedCallersOnly] private static nint TypeClass(nint type) => type == UiType ? Current.uiClass : Current.menuClass;
    [UnmanagedCallersOnly] private static uint PropertyCount(nint type) => 1;
    [UnmanagedCallersOnly] private static byte WidgetVisible(nint widget)
    { Check.Equal(Current.menu, widget); return *(byte*)(widget + 0x40); }
    [UnmanagedCallersOnly] private static uint TypeSize(nint type) => 0x100;
    [UnmanagedCallersOnly] private static int Property(nint type, uint index, nint* name, int* id, byte* isPrivate, byte* isProtected,
        int* offset, byte* reference, uint* access, int* composite, byte* indirect)
    {
        *name = Current.Text(type == MenuType ? "bIsOpened" : "ShowDrainFluidsTip_TopTips");
        *id = type == MenuType ? 1 : 0x4800367c; *offset = 0x40; *reference = 0; *composite = 0; *indirect = 0;
        return 0;
    }
    [UnmanagedCallersOnly] private static void AllWidgets(nint world, Delegate* widgets, nint* type, byte topLevel)
    {
        Check.Equal((nint)100, world); Check.Equal(Current.menuClass, *type); Check.Equal((byte)0, topLevel);
        if (Current.missingMenu) return;
        widgets->Data = (Binding*)NativeMemory.Alloc(8); Current.gameBuffers.Add((nint)widgets->Data);
        *(nint*)widgets->Data = Current.menu; widgets->Count = widgets->Capacity = 1;
    }
    [UnmanagedCallersOnly] private static uint FunctionCount(nint module) => 1;
    [UnmanagedCallersOnly] private static nint ModuleFunction(nint module, uint index) => Language;
    [UnmanagedCallersOnly] private static nint FunctionName(nint function) => Current.Text("GetLanguage");
    [UnmanagedCallersOnly] private static nint FunctionNamespace(nint function) => Current.Text("GlobalFunctionLibrary");
    [UnmanagedCallersOnly] private static nint FunctionOwner(nint function) => Current.Text(function == SetText ? "U_UI_TopTips" : "U_UICharacterState");
    [UnmanagedCallersOnly] private static uint ParameterCount(nint function) => function == Clear || function == Show ? 0U : function == SetText ? 2U : function == Progress ? 3U : 1U;
    [UnmanagedCallersOnly] private static int Parameter(nint function, uint index, int* type, uint* modifiers, nint* name, nint* defaultValue)
    {
        *type = function == Language || (function == SetText && index == 0) ? 0x400044 : function == Progress ? (Current.wrongProgressType && index == 1 ? 10 : 11) : 1;
        *modifiers = function == Language || (function == SetText && index == 0) ? Current.languageModifiers : Current.valueModifiers;
        if (function == SetText && Current.wrongTipParameter && index == 1) *type = 11;
        return 0;
    }
    [UnmanagedCallersOnly] private static int ReturnType(nint function, uint* modifiers)
    { if (modifiers != null) *modifiers = 0; return function == Language ? 0x400044 : 0; }
    [UnmanagedCallersOnly] private static nint TypeById(nint engine, int id) => id == 0x4800367c ? TipType : StringType;
    [UnmanagedCallersOnly] private static nint Method(nint type, byte* name) => Marshal.PtrToStringUTF8((nint)name) switch
    { "ShowDrainFluidsTip" => Show, "SetText" => SetText, "ClearCurrentCharacterFluidsData" => Clear, "DelaySetFluidProgress" => Current.missingResetMethod ? 0 : Progress, "SetReleaseFluidsBtnEnabled" => Button, _ => 0 };
    [UnmanagedCallersOnly] private static int Prepare(nint context, nint function) { Current.prepared = function; return 0; }
    [UnmanagedCallersOnly] private static int SetObject(nint context, nint instance) { Check.Equal(Current.prepared == SetText ? Current.tip : Current.widget, instance); return 0; }
    [UnmanagedCallersOnly] private static int SetAddress(nint context, uint index, nint argument)
    { Check.Equal(0U, index); if (Current.prepared == SetText) Current.topText = Read((GameString*)argument); else Current.languageKey = Read((GameString*)argument); return 0; }
    [UnmanagedCallersOnly] private static int SetDouble(nint context, uint index, double value) { Check.Equal(index == 0 ? 0d : index == 1 ? .5 : .01, value); return 0; }
    [UnmanagedCallersOnly] private static int SetByte(nint context, uint index, byte value) { Check.Equal((byte)0, value); return 0; }
    [UnmanagedCallersOnly] private static int Execute(nint context)
    {
        if (Current.prepared == Language)
        {
            if (Current.languageException) return 3;
            Current.Return(Current.languageKey == "RoleReleseLiquid" ? Current.unsupportedTemplate ? "unsupported" : "{0} expelled {1}" : "Translated " + Current.languageKey);
        }
        else if (Current.prepared == Clear) Current.clearCalls++;
        else if (Current.prepared == Progress) Current.progressCalls++;
        else if (Current.prepared == Button) Current.buttonCalls++;
        else if (Current.prepared == Show)
        { Current.shows++; *(nint*)(Current.widget + 0x40) = Current.missingTip ? 0 : Current.tip; }
        else if (Current.prepared == SetText) Current.topCalls++;
        return 0;
    }
    [UnmanagedCallersOnly] private static nint ReturnObject(nint context) => Current.result;
    [UnmanagedCallersOnly] private static nint ExceptionString(nint context) => Current.Text("synthetic localization exception");
    [UnmanagedCallersOnly] private static int ExceptionLine(nint context, int* column, nint* section) { *column = 1; *section = 0; return 1; }
    [UnmanagedCallersOnly] private static nint GameAllocate(nuint bytes, uint alignment) { nint p = (nint)NativeMemory.Alloc(bytes); Current.gameBuffers.Add(p); return p; }
    [UnmanagedCallersOnly] private static void GameFree(void* value) { Check.True(Current.gameBuffers.Remove((nint)value), "Freed unowned delegate storage."); NativeMemory.Free(value); }
    [UnmanagedCallersOnly] private static nint GetWeak(ulong* weak) => *weak == 0 ? 0 : *weak == 1 ? Current.widget : Current.other;
    [UnmanagedCallersOnly] private static void Broadcast(nint nativeDelegate, GameString* role)
    {
        if (nativeDelegate == Current.originalDelegate) { Current.originalBroadcasts++; return; }
        var copy = (Delegate*)nativeDelegate; Check.Equal(2, copy->Count); Current.nativeCallbacks += copy->Count;
        if (Current.shrinkCopy)
        {
            Check.True(Current.gameBuffers.Remove((nint)copy->Data), "Expected game-owned array.");
            NativeMemory.Free(copy->Data); copy->Data = (Binding*)NativeMemory.Alloc(16); copy->Count = copy->Capacity = 1;
            Current.gameBuffers.Add((nint)copy->Data);
        }
    }
    [UnmanagedCallersOnly] private static void ShowRight(nint subsystem, byte type, GameString* player, GameString* id, GameString* message, GameString* number)
    { Current.rightNumber = Read(number); Current.rightCalls++; }
}
