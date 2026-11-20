using System.Runtime.InteropServices;
using System.Text;

namespace Dollhouse.Gameplay;

internal sealed unsafe partial class NativeGame
{
    private readonly record struct ScriptMember(int Offset, int TypeId);
    private bool TryScriptMember(nint type, string requested, int bytes, out ScriptMember member)
    {
        member = default;
        uint count = ((delegate* unmanaged<nint, uint>)At(SupportedBuild.ScriptPropertyCount))(type);
        uint size = ((delegate* unmanaged<nint, uint>)At(SupportedBuild.ScriptTypeSize))(type);
        for (uint i = 0; i < count; i++)
        {
            nint name = 0;
            int id = 0, offset = -1, composite = -1;
            byte reference = 0, indirect = 0;
            int status = ((delegate* unmanaged<nint, uint, nint*, int*, byte*, byte*, int*, byte*, uint*, int*, byte*, int>)
                At(SupportedBuild.ScriptProperty))(type, i, &name, &id, null, null, &offset, &reference, null, &composite, &indirect);
            if (status < 0 || ScriptString(name) != requested) continue;
            if (offset < 0x28 || (ulong)offset + (uint)bytes > size || reference != 0 || composite != 0 || indirect != 0)
            { LogCumflation($"script property rejected: name={requested}, type={id:x}, offset={offset}, size={size}"); return false; }
            member = new(offset, id);
            return true;
        }
        LogCumflation($"script property missing: {requested}");
        return false;
    }

    private static string ScriptString(nint value) => value == 0 ? "" : Marshal.PtrToStringUTF8(value) ?? "";

    private nint FindScriptType(nint engine, string moduleName, string typeName)
    {
        byte[] moduleText = Encoding.UTF8.GetBytes(moduleName + '\0');
        byte[] typeText = Encoding.UTF8.GetBytes(typeName + '\0');
        fixed (byte* moduleBytes = moduleText, typeBytes = typeText)
        {
            // ONLY_IF_EXISTS=0: source labels are now verified by user logs, and the startup
            // retention patch preserves the map needed for this ordinary registry lookup.
            nint module = ((delegate* unmanaged<nint, byte*, int, nint>)At(SupportedBuild.ScriptGetModule))(
                engine, moduleBytes, 0);
            if (module == 0)
            {
                LogCumflation($"experiment script module absent: {moduleName}");
                return 0;
            }
            if (((delegate* unmanaged<nint, nint>)At(SupportedBuild.ScriptModuleEngine))(module) != engine)
            {
                LogCumflation($"experiment script module rejected: {moduleName} belongs to a different engine");
                return 0;
            }
            nint type = ((delegate* unmanaged<nint, byte*, nint>)At(SupportedBuild.ScriptModuleFindType))(module, typeBytes);
            bool valid = type != 0 &&
                ((delegate* unmanaged<nint, nint>)At(SupportedBuild.ScriptTypeEngine))(type) == engine &&
                ScriptString(((delegate* unmanaged<nint, nint>)At(SupportedBuild.ScriptTypeName))(type)) == typeName;
            if (!valid) LogCumflation($"script type unavailable: {moduleName}.{typeName}");
            return valid ? type : 0;
        }
    }

    // UObject::IsA's fast ancestor-array check, as used by GetRoomManagerByPlayerId itself.
    // No owner walk or cached class/object pointer. Only live native objects and UClass metadata.
    private static bool IsNativeInstance(nint instance, nint expectedClass)
    {
        if (instance == 0 || expectedClass == 0) return false;
        nint actualClass = *(nint*)(instance + SupportedBuild.ObjectClass);
        if (actualClass == 0) return false;
        int expectedDepth = *(int*)(expectedClass + SupportedBuild.ClassDepth);
        int actualDepth = *(int*)(actualClass + SupportedBuild.ClassDepth);
        if (expectedDepth < 0 || actualDepth < expectedDepth) return false;
        nint ancestors = *(nint*)(actualClass + SupportedBuild.ClassAncestors);
        return ancestors != 0 && ((nint*)ancestors)[expectedDepth] == expectedClass + SupportedBuild.ClassAncestors;
    }

    private nint NativeClassForScriptType(nint type) =>
        ((delegate* unmanaged<nint, nint>)At(SupportedBuild.ScriptTypeNativeClass))(type);

    private bool TryReadPerformer(nint room, nint roomType, nint performerType, out nint performer, out int performerOffset)
    {
        performer = 0;
        performerOffset = -1;
        nint roomClass = NativeClassForScriptType(roomType);
        if (!IsNativeInstance(room, roomClass))
        {
            LogCumflation("experiment room rejected: class does not match A_RoomManager");
            return false;
        }
        if (!TryScriptMember(roomType, "HPerformer", 8, out var member)) return false;
        int expectedType = ((delegate* unmanaged<nint, int>)At(SupportedBuild.ScriptTypeId))(performerType);
        if ((member.TypeId & 0x1fffffff) != (expectedType & 0x1fffffff))
        { LogCumflation($"HPerformer type rejected: actual={member.TypeId:x}, expected={expectedType:x}"); return false; }
        performerOffset = member.Offset;
        performer = *(nint*)(room + member.Offset);
        if (IsNativeInstance(performer, NativeClassForScriptType(performerType))) return true;
        LogCumflation("room performer absent or wrong class");
        return false;
    }

    private nint FindUpdateBulge(nint performerType)
    {
        ReadOnlySpan<byte> name = "UpdateBulge\0"u8;
        nint function;
        fixed (byte* text = name)
            function = ((delegate* unmanaged<nint, byte*, nint>)At(SupportedBuild.ScriptMethodByName))(performerType, text);
        if (function == 0)
        {
            LogCumflation("experiment UpdateBulge method absent");
            return 0;
        }
        string owner = ScriptString(((delegate* unmanaged<nint, nint>)At(SupportedBuild.ScriptFunctionObjectName))(function));
        string functionName = ScriptString(((delegate* unmanaged<nint, nint>)At(SupportedBuild.ScriptFunctionName))(function));
        uint parameters = ((delegate* unmanaged<nint, uint>)At(SupportedBuild.ScriptParameterCount))(function);
        int returnType = ((delegate* unmanaged<nint, uint*, int>)At(SupportedBuild.ScriptReturnType))(function, null);
        bool valid = owner == "A_HPerformer" && functionName == "UpdateBulge" && parameters == 0 && returnType == 0;
        if (!valid) LogCumflation($"UpdateBulge signature rejected: {owner}::{functionName}, parameters={parameters}, returnType={returnType}");
        return valid ? function : 0;
    }
}
