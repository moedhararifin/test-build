namespace Dollhouse.Gameplay;

/// <summary>Game operations isolated from the patch's policy for offline verification.</summary>
public interface IItemUseGame
{
    bool IsFullUnlock { get; }
    int ItemId(nint request);
    int RecoveryType(nint request);
    bool HasRole(nint request);
    long InventoryCount(int itemId);
    void HandleLocalItemUse(nint bag, nint request);
    nint FindRolesProxy(nint bag);
    void NotifyRoleItemUse(nint roles, nint request);
    bool ResetDilation(nint roles, nint request);
    bool RecoverStamina(nint request);
    void ItemUseFailed(string message);
    void AddAffinity(nint bag, nint request, int amount);
    void NotifyItemUseUI(nint bag, nint request);
}

/// <summary>The injected behavior. No offsets, opcodes, or global mode overrides.</summary>
public static class ItemUsePatch
{
    public const int Aphrodisiac = 99982;
    public const int RepairGel = 99984;

    public static void UseItem(IItemUseGame game, nint bag, nint request)
    {
        if (bag == 0 || request == 0) return;
        // Reject malformed reset requests before native inventory consumption.
        if (game.ItemId(request) == RepairGel && !TryRepairTarget(game, bag, request, out _)) return;
        // Bypass the mode gate at Req_UseItem only. Native validation/consumption stays in place.
        game.HandleLocalItemUse(bag, request);
    }

    public static long AvailableStock(IItemUseGame game, int itemId)
    {
        // Only the item-use handler calls this hook. Other inventory consumers are unchanged.
        if ((itemId is Aphrodisiac or RepairGel) && game.IsFullUnlock) return 999;
        return game.InventoryCount(itemId);
    }

    public static void CompleteSuccessfulUse(IItemUseGame game, nint bag, nint request)
    {
        // Installed at the successful ordinary-item branch, after native stock validation.
        if (bag == 0 || request == 0) return;
        switch (game.ItemId(request))
        {
            case Aphrodisiac:
                if (!game.HasRole(request)) return;
                var roles = game.FindRolesProxy(bag);
                if (roles == 0) return;
                // The existing room listener handles aphrodisiacs only.
                game.NotifyRoleItemUse(roles, request);
                break;
            case RepairGel:
                if (!TryRepairTarget(game, bag, request, out var target)) return;
                if (game.RecoveryType(request) == 2)
                {
                    if (!game.RecoverStamina(request)) return;
                }
                else if (!game.ResetDilation(target, request)) return;
                break;
            default:
                return;
        }
        game.AddAffinity(bag, request, 3); // Existing native daily/level caps remain in charge.
        game.NotifyItemUseUI(bag, request);
    }

    private static bool TryRepairTarget(IItemUseGame game, nint bag, nint request, out nint roles)
    {
        roles = 0;
        if (!game.HasRole(request))
        {
            game.ItemUseFailed("Repair Gel rejected: missing or invalid role string");
            return false;
        }
        int opening = game.RecoveryType(request);
        if (opening is not (0 or 1 or 2))
        {
            game.ItemUseFailed($"Repair Gel rejected: unsupported RecoverType={opening}; expected 0 vaginal, 1 anal or 2 stamina");
            return false;
        }
        roles = game.FindRolesProxy(bag);
        if (roles != 0) return true;
        game.ItemUseFailed("Repair Gel rejected: local roles proxy unavailable");
        return false;
    }
}
