namespace Dollhouse.Gameplay;

internal sealed unsafe partial class NativeGame
{
    private int RoleAttribute(GameString* role, int attribute) =>
        ((delegate* unmanaged<GameString*, int, int>)At(SupportedBuild.ReadRoleAttribute))(role, attribute);

    private void SetRoleAttribute(GameString* role, int attribute, int value) =>
        ((delegate* unmanaged<GameString*, int, int, void>)At(SupportedBuild.WriteRoleAttribute))(role, attribute, value);

    internal void HandleRolePropertyChanges(int playerId, RolePropertyChanges* changes, nint context = 0)
    {
        if (changes == null) return;
        try { PersistRoleAttributes(playerId, changes, context); }
        finally
        {
            HandleStaminaPropertyChanges(playerId, changes);
        }
    }

    private void PersistRoleAttributes(int playerId, RolePropertyChanges* changes, nint context)
    {
        int localPlayer = ((delegate* unmanaged<int>)At(SupportedBuild.LocalPlayerId))();
        // The native handler accepts 0 as local context. Never write another player's attributes to this profile.
        if (playerId != 0 && playerId != localPlayer) return;
        if (changes->Data == null || changes->Length < 0 || changes->Length > changes->Capacity
            || changes->Length > 4096) return;
        for (int index = 0; index < changes->Length; index++)
        {
            var change = changes->Data + index;
            if (change->Role.Data == null || change->Role.Length <= 1 || change->Role.Length > change->Role.Capacity) continue;
            // Lifetime statistics are independent of current body contents and its toggle.
            PersistSexStats(change);
            change->Attributes.TryGet(CumflationPatch.WombAttribute, out int addedWomb);
            change->Attributes.TryGet(CumflationPatch.GiAttribute, out int addedGi);
            if (addedWomb <= 0 && addedGi <= 0) continue;

            // This option gates new stored volume only. Existing contents and the native
            // clear/drain path are untouched, as is the favor/stamina callback in finally.
            if (!CumflationEnabled) continue; // Later records can still carry lifetime statistics.

            int capacity = RoleAttribute(&change->Role, CumflationPatch.CapacityAttribute);
            int womb = RoleAttribute(&change->Role, CumflationPatch.WombAttribute);
            int gi = RoleAttribute(&change->Role, CumflationPatch.GiAttribute);
            var next = CumflationPatch.Accumulate(capacity, womb, gi, addedWomb, addedGi);
            // This writer updates the engine's cache and RoleAttrs.txt, just as the native drain routine does.
            if (next.Womb != womb) SetRoleAttribute(&change->Role, CumflationPatch.WombAttribute, next.Womb);
            if (next.Gi != gi) SetRoleAttribute(&change->Role, CumflationPatch.GiAttribute, next.Gi);
            if (context != 0 && (next.Womb != womb || next.Gi != gi))
            {
                nint roles = FindRoomRolesProxy(context, localPlayer);
                if (roles != 0) NotifyRoomVolumes(roles, localPlayer, &change->Role);
                else LogCumflation("stored volumes, but the room proxy was unavailable");
            }
        }
    }
}
