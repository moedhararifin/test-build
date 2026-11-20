namespace Dollhouse.Gameplay;

internal sealed unsafe partial class NativeGame
{
    private nint FindRoomRolesProxy(nint context, int playerId)
    {
        if (context == 0) return 0;
        nint type = ((delegate* unmanaged<nint>)At(SupportedBuild.RolesClass))();

        // Preserve native room selection and the already-working clear context.
        nint room = FindPlayerRoleProxy(context, playerId, type);
        if (room != 0) return room;

        // This build creates the action proxy as a rooted singleton in /Engine/Transient,
        // not under PlayerClientInstance. It has neither a world nor a client-manager owner.
        // Gameplay callbacks run on the game thread: use the engine's current GWorld, the
        // same world symbol its own context resolver uses. Read afresh on every notification.
        nint world = *(nint*)At(SupportedBuild.CurrentWorld);
        if (world == 0)
        {
            LogCumflation("active gameplay world is unavailable");
            return 0;
        }
        room = FindPlayerRoleProxy(world, playerId, type);
        return room;
    }

    private nint FindPlayerRoleProxy(nint context, int playerId, nint type) =>
        ((delegate* unmanaged<nint, int, nint, nint>)At(SupportedBuild.FindPlayerProxy))(context, playerId, type);
}
