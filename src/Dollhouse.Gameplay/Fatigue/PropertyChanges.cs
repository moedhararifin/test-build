namespace Dollhouse.Gameplay;

internal sealed unsafe partial class NativeGame
{
    internal void HandleStaminaPropertyChanges(int playerId, RolePropertyChanges* changes)
    {
        if (changes == null || changes->Length < 0 || changes->Length > changes->Capacity ||
            changes->Length > 4096 || (changes->Length != 0 && changes->Data == null)) return;
        int local = ((delegate* unmanaged<int>)At(SupportedBuild.LocalPlayerId))();
        if (playerId != 0 && playerId != local) return;
        bool enabled = FatigueEnabled;
        if (((delegate* unmanaged<byte>)At(SupportedBuild.LocalProgressionGate))() != 0)
        {
            // Preserve native affinity and all other local-handler behavior. OFF
            // filters costs in a borrowed, synchronous copy; never edit/free the
            // original FString/TMap or pass this copy to a multicast dispatcher.
            if (enabled)
                ((delegate* unmanaged<int, RolePropertyChanges*, void>)At(SupportedBuild.LocalRolePropertyChange))(playerId, changes);
            else
            {
                var records = new ReadOnlySpan<RolePropertyChange>(changes->Data, changes->Length).ToArray();
                foreach (ref var record in records.AsSpan()) record.SpentStamina = FatiguePolicy.EffectiveCost(false, record.SpentStamina);
                fixed (RolePropertyChange* data = records)
                {
                    RolePropertyChanges filtered = new() { Data = data, Length = records.Length, Capacity = records.Length };
                    ((delegate* unmanaged<int, RolePropertyChanges*, void>)At(SupportedBuild.LocalRolePropertyChange))(playerId, &filtered);
                }
            }
            return; // Native already charged once; do not also invoke DeductStamina.
        }
        fixed (char* reason = "RolePropertyChange")
            for (int i = 0; i < changes->Length; i++)
            {
                int cost = FatiguePolicy.EffectiveCost(enabled, changes->Data[i].SpentStamina);
                if (cost != 0) ChangeStamina(&changes->Data[i].Role, cost, reason);
            }
    }
}
