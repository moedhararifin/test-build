namespace Dollhouse.Gameplay;

internal sealed unsafe partial class NativeGame
{
    private void ShowCardAreaLock(nint pose, CardFilterArray<byte>* areas)
    {
        // Native IsLock also gates clicks. Ownership/experience flags remain native.
        *(byte*)(pose + CardFilteringBindings.PoseLocked) = 1;
        ShowCardFilterControl(pose, CardFilteringBindings.PoseTiredGroup, false);
        ShowCardFilterControl(pose, CardFilteringBindings.PoseLockGroup, true);
        ShowCardFilterControl(pose, CardFilteringBindings.PoseUsableAreas, true);
        SetCardFilterText(*(nint*)(pose + CardFilteringBindings.PoseLockReason), "Required prop unavailable in this area.");
        ShowCardUsableAreas(pose, areas);
    }

    private void ShowCardUsableAreas(nint pose, CardFilterArray<byte>* areas)
    {
        nint type = *(nint*)(pose + SupportedBuild.ObjectClass);
        ulong name = *(ulong*)At(CardFilteringBindings.UsableAreasName);
        nint function = ((delegate* unmanaged<nint, ulong, int, nint>)At(CardFilteringBindings.FindWidgetFunction))
            (type, name, 1);
        if (function == 0)
            throw new InvalidDataException("Card widget has no SetUsableInIcon function.");

        // The reflected parameter is a by-value TArray<RoomType>. Give the event
        // an owned native copy and release it after synchronous dispatch.
        CardFilterArray<byte> parameters = default;
        try
        {
            ReadOnlySpan<byte> values = areas->Span;
            if (!values.IsEmpty)
            {
                parameters.Data = (byte*)((delegate* unmanaged<nuint, uint, void*>)At(SupportedBuild.Allocate))
                    ((nuint)values.Length, 0);
                if (parameters.Data == null) throw new OutOfMemoryException("Card area-icon parameters.");
                parameters.Count = parameters.Capacity = values.Length;
                values.CopyTo(new Span<byte>(parameters.Data, parameters.Count));
            }
            var process = (delegate* unmanaged<nint, nint, CardFilterArray<byte>*, void>)
                (*(nint*)(*(nint*)pose + CardFilteringBindings.ProcessEventSlot));
            process(pose, function, &parameters);
        }
        finally
        {
            if (parameters.Data != null) ((delegate* unmanaged<void*, void>)At(SupportedBuild.Free))(parameters.Data);
        }
    }
}
