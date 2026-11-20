namespace Dollhouse.Gameplay;

internal sealed unsafe partial class NativeGame
{
    internal void RefreshExperimentBulges(int playerId)
    {
        nint world = *(nint*)At(SupportedBuild.CurrentWorld);
        if (world == 0) return;
        nint gameMode = ((delegate* unmanaged<nint, nint>)At(SupportedBuild.GetGameMode))(world);
        nint roomModeClass = ((delegate* unmanaged<nint>)At(SupportedBuild.RoomGameModeClass))();
        if (!IsNativeInstance(gameMode, roomModeClass))
            return; // Normal outside an experiment room.
        nint room = 0;
        byte found = ((delegate* unmanaged<nint, int, nint*, byte>)At(SupportedBuild.GetRoomManager))(
            gameMode, playerId, &room);
        if (found == 0 || room == 0) return;
        TryRefreshRoomPerformer(world, room);
    }
}
