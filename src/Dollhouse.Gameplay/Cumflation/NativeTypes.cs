using System.Runtime.InteropServices;

namespace Dollhouse.Gameplay;

// Exact native FRolePropertyChange/TArray layouts. The caller retains ownership throughout the callback.
[StructLayout(LayoutKind.Sequential)]
internal struct RolePropertyChange
{
    public GameString Role;
    public NativeAttributeMap Attributes;
    public int AddedFavor;
    public int SpentStamina;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct RolePropertyChanges
{
    public RolePropertyChange* Data;
    public int Length;
    public int Capacity;
}

// TMap<ERoleAttr,int> is a sparse TSet, not a packed array. Slots can contain freed entries.
// Layout verified against FillRoleAttrs (RVA 0x53f11f0); never mutate/free the request map.
[StructLayout(LayoutKind.Explicit, Size = 0x50)]
internal unsafe struct NativeAttributeMap
{
    [FieldOffset(0)] public byte* Elements;
    [FieldOffset(8)] public int SlotCount;
    [FieldOffset(12)] public int SlotCapacity;
    [FieldOffset(0x10)] public fixed uint InlineAllocationFlags[4];
    [FieldOffset(0x20)] public uint* HeapAllocationFlags;
    [FieldOffset(0x28)] public int AllocationBitCount;
    [FieldOffset(0x2c)] public int AllocationBitCapacity;
    [FieldOffset(0x30)] public int FirstFreeIndex;
    [FieldOffset(0x34)] public int FreeCount;
    [FieldOffset(0x40)] public void* HeapHash;

    // Match the native empty TSet constructor before calling the native element builder.
    public static NativeAttributeMap Empty => new() { AllocationBitCapacity = 128, FirstFreeIndex = -1 };

    public readonly bool TryGet(int attribute, out int value)
    {
        value = 0;
        if (Elements == null || SlotCount < 0 || SlotCount > SlotCapacity || SlotCount > 4096
            || AllocationBitCount != SlotCount) return false;
        fixed (uint* inline = InlineAllocationFlags)
        {
            uint* flags = HeapAllocationFlags == null ? inline : HeapAllocationFlags;
            if (HeapAllocationFlags == null && SlotCount > 128) return false;
            for (int index = 0; index < SlotCount; index++)
            {
                if ((flags[index / 32] & (1u << (index % 32))) == 0) continue;
                byte* entry = Elements + index * 0x10;
                if (*entry != attribute) continue;
                value = *(int*)(entry + 4);
                return true;
            }
        }
        return false;
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct LocalRoleAttributesChanged
{
    public GameString Role;
    public NativeAttributeMap Attributes;
}

[StructLayout(LayoutKind.Sequential)]
internal struct RoomRoleAttributesChanged
{
    public int PlayerId;
    public GameString Role;
    public NativeAttributeMap Attributes;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct AttributePairReferences
{
    public byte* Key;
    public int* Value;
}
