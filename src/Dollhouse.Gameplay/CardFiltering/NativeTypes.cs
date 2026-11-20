using System.Runtime.InteropServices;

namespace Dollhouse.Gameplay;

// TArray ABI view. Matching cards and selections are borrowed; panel-query and
// reflected-event parameter buffers have explicit ownership at their call sites.
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct CardFilterArray<T> where T : unmanaged
{
    public T* Data;
    public int Count, Capacity;

    internal readonly ReadOnlySpan<T> Span => Count < 0 || Capacity < Count || (Count > 0 && Data == null)
        ? throw new InvalidDataException("Invalid card-filter array.")
        : new ReadOnlySpan<T>(Data, Count);
}

// Native FText, not FString. The virtual SetText call consumes its owned reference.
[StructLayout(LayoutKind.Sequential)]
internal struct CardFilterText
{
    public nint Data;
    public int Flags, Padding;
}
