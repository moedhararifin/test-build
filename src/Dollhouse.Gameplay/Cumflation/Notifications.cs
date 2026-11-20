namespace Dollhouse.Gameplay;

internal sealed unsafe partial class NativeGame
{
    // Saving alone does not update F_S_RoomRolesStat. The existing room listener computes
    // fullness and forwards OnFemaleStatChanged. Experiments also need their explicit visual refresh.
    private void NotifyRoomVolumes(nint roles, int playerId, GameString* role)
    {
        RoomRoleAttributesChanged notification = new()
        {
            PlayerId = playerId,
            Attributes = NativeAttributeMap.Empty
        };
        int womb = RoleAttribute(role, CumflationPatch.WombAttribute);
        int gi = RoleAttribute(role, CumflationPatch.GiAttribute);
        try
        {
            Copy(&notification.Role, role);
            AddNotificationAttribute(&notification.Attributes, CumflationPatch.WombAttribute, womb);
            AddNotificationAttribute(&notification.Attributes, CumflationPatch.GiAttribute, gi);
            ((delegate* unmanaged<nint, void*, void>)At(SupportedBuild.Broadcast))(
                roles + SupportedBuild.RoomRoleAttributesDelegate, &notification);
        }
        finally
        {
            Free(&notification.Role);
            FreeAllocation(notification.Attributes.Elements);
            FreeAllocation(notification.Attributes.HeapAllocationFlags);
            FreeAllocation(notification.Attributes.HeapHash);
        }
        // Zero transitions belong to the native draining animation; do not snap that animation
        // to its final size. Positive changes refresh from the newly published room state.
        if (womb > 0 || gi > 0) RefreshExperimentBulges(playerId);
    }

    private void AddNotificationAttribute(NativeAttributeMap* map, int attribute, int value)
    {
        byte key = checked((byte)attribute);
        AttributePairReferences pair = new() { Key = &key, Value = &value };
        int element = -1;
        // Construct a real native map, including sparse slots and hash buckets. The event carries
        // absolute stored values, not the experiment's deltas, and owns no request memory.
        ((delegate* unmanaged<NativeAttributeMap*, int*, AttributePairReferences*, byte*, void>)
            At(SupportedBuild.AddRoleAttribute))(map, &element, &pair, null);
    }

    private void FreeAllocation(void* allocation)
    {
        if (allocation != null) ((delegate* unmanaged<void*, void>)At(SupportedBuild.Free))(allocation);
    }

    internal void NotifyVolumesCleared(nint localDelegate, LocalRoleAttributesChanged* notification)
    {
        try
        {
            if (localDelegate == 0 || notification == null) return;
            CancelExperimentBulgeGrowth(); // Native draining owns subsequent visual changes.
            // Preserve preview/UI subscribers and the native clear operation's owned parameters.
            ((delegate* unmanaged<nint, void*, void>)At(SupportedBuild.Broadcast))(localDelegate, notification);
            if (notification->Role.Data == null || notification->Role.Length <= 1)
                return;
            int localPlayer = ((delegate* unmanaged<int>)At(SupportedBuild.LocalPlayerId))();
            nint context = localDelegate - SupportedBuild.LocalRoleAttributesDelegate;
            nint roomRoles = FindRoomRolesProxy(context, localPlayer);
            if (roomRoles != 0) NotifyRoomVolumes(roomRoles, localPlayer, &notification->Role);
            else LogCumflation("cleared volumes, but the room proxy was unavailable");
        }
        finally { CompleteDrainNotice(); }
    }

}
