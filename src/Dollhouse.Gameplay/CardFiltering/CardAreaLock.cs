namespace Dollhouse.Gameplay;

internal sealed unsafe partial class NativeGame
{
    internal int SetCardLock(nint pose, byte unowned, byte experience)
    {
        int original = ((delegate* unmanaged<nint, byte, byte, int>)At(CardFilteringBindings.OriginalCardLock))
            (pose, unowned, experience);
        if (pose == 0 || original != 0) return original; // Never undo an existing native lock.
        try
        {
            nint card = pose + CardFilteringBindings.PoseData;
            byte location = *(byte*)(card + CardFilteringBindings.CardLocation);
            var areas = (CardFilterArray<byte>*)(card + CardFilteringBindings.CardAreas);
            if (!CardAreaPolicy.HasAreaRestriction(location, areas->Span) || areas->Span.Contains(ReadCurrentCardArea()))
                return original;

            ShowCardAreaLock(pose, areas);
            return 1;
        }
        catch (Exception error)
        {
            LogCardFiltering("area lock", error);
            return *(byte*)(pose + CardFilteringBindings.PoseLocked) != 0 ? 1 : original;
        }
    }

    private byte ReadCurrentCardArea()
    {
        GameString map = default;
        try
        {
            ((delegate* unmanaged<GameString*, GameString*>)At(CardFilteringBindings.CurrentMapName))(&map);
            if (map.Length <= 1 || map.Length > map.Capacity || map.Data == null)
                throw new InvalidDataException("Current card area is unavailable.");
            var name = new ReadOnlySpan<char>(map.Data, map.Length - 1);
            if (!CardAreaPolicy.TryGetArea(name, out byte area))
                throw new InvalidDataException($"Current card area is unrecognized: {new string(name)}.");
            return area;
        }
        finally { Free(&map); }
    }
}
