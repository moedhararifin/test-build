namespace Dollhouse.Gameplay;

internal sealed unsafe partial class NativeGame
{
    private void PersistSexStats(RolePropertyChange* change)
    {
        for (int attribute = SexStatsPatch.FirstAttribute; attribute <= SexStatsPatch.LastAttribute; attribute++)
        {
            if (!change->Attributes.TryGet(attribute, out int delta) || delta == 0) continue;
            int current = RoleAttribute(&change->Role, attribute);
            int next = SexStatsPatch.Accumulate(current, delta);
            if (next != current) SetRoleAttribute(&change->Role, attribute, next);
        }
    }
}
