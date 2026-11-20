using System.Runtime.InteropServices;

namespace Dollhouse.Gameplay;

// These are ABI records, not managed copies of Unreal objects. Borrowed pointers never escape a call.
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct GameString
{
    public char* Data;
    public int Length;
    public int Capacity;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ItemRequest
{
    public GameString Role;
    public int ItemId;
    public int Count;
    public int RecoverType; // Repair Gel: 0 vaginal, 1 anal; other recovery actions are separate.
    public int Reserved; // Preserve native trailing padding when copying the request for UI listeners.
}

[StructLayout(LayoutKind.Sequential)]
internal struct RoleUseEvent
{
    public int Result;
    public int ItemId;
    public GameString Role;
}

// A partial view of FFrame. We only touch Code; the engine owns the full object.
[StructLayout(LayoutKind.Explicit)]
internal struct ScriptFrame
{
    [FieldOffset(SupportedBuild.ScriptFrameCode)]
    public nint Code;

    public void FinishArguments()
    {
        // FFrame::Code is null for the compiled-in parameter path; otherwise skip EX_EndFunctionParms.
        if (Code != 0) Code++;
    }
}
