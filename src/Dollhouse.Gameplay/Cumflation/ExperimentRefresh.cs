namespace Dollhouse.Gameplay;

internal sealed unsafe partial class NativeGame
{
    private bool TryRefreshRoomPerformer(nint world, nint room) => WithScriptContext(world, (context, engine) =>
    {
        nint roomType = FindScriptType(engine, "Core.BP.RoomManager", "A_RoomManager");
        nint performerType = FindScriptType(engine, "Core.BP.H.Common.HPerformer", "A_HPerformer");
        if (roomType == 0 || performerType == 0 ||
            !TryReadPerformer(room, roomType, performerType, out nint performer, out int offset)) return false;
        nint update = FindUpdateBulge(performerType);
        if (update == 0) return false;

        var growth = CaptureExperimentBulge(context, engine, world, room, performer, performerType, offset);
        if (!PrepareScriptCall(context, update, performer, "UpdateBulge") ||
            !ExecuteScriptCall(context, "UpdateBulge")) return false;
        BeginExperimentBulgeGrowth(growth);
        return true;
    });
}
