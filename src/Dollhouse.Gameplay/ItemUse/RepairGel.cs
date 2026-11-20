namespace Dollhouse.Gameplay;

// Repair Gel uses the native persisted setter and its dilation-change notification.
internal sealed unsafe partial class NativeGame
{
    public bool ResetDilation(nint roles, nint request)
    {
        var role = &((ItemRequest*)request)->Role;
        byte anal = checked((byte)RecoveryType(request)); // Policy accepts only 0/1 before this call.
        string opening = anal == 0 ? "vaginal" : "anal";
        string stage = "read";
        try
        {
            var read = (delegate* unmanaged<GameString*, byte, int>)At(SupportedBuild.ReadRoleDilation);
            int before = read(role, anal);
            if (before < 0)
            {
                ItemUseFailed($"Repair Gel reset rejected: opening={opening}, negative stored dilation={before}");
                return false;
            }
            // Native storage packs level*10000 + progress. Subtract the whole value;
            // the native method persists Dilation.txt and broadcasts its change event.
            stage = "native reset";
            byte applied = ((delegate* unmanaged<nint, GameString*, byte, int, byte>)At(SupportedBuild.AddRoleDilation))
                (roles, role, anal, -before);
            stage = "readback";
            int after = read(role, anal);
            if (applied != 0 && after == 0) return true;
            ItemUseFailed($"Repair Gel reset failed: opening={opening}, before={before}, after={after}, nativeAccepted={applied != 0}; affinity/UI success skipped");
        }
        catch (Exception error)
        {
            ItemUseFailed($"Repair Gel reset failed: stage={stage}, opening={opening}, error={error.GetType().Name}; affinity/UI success skipped");
        }
        return false;
    }
}
