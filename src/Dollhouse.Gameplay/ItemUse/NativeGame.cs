namespace Dollhouse.Gameplay;

internal sealed unsafe partial class NativeGame : IItemUseGame
{
    public bool IsFullUnlock => ((delegate* unmanaged<byte>)At(SupportedBuild.ProfileMode))() == 0;
    public int ItemId(nint request) => ((ItemRequest*)request)->ItemId;
    public int RecoveryType(nint request) => ((ItemRequest*)request)->RecoverType;
    public bool HasRole(nint request)
    {
        var role = &((ItemRequest*)request)->Role;
        return role->Data != null && role->Length > 1 && role->Length <= role->Capacity;
    }
    public long InventoryCount(int itemId) => ((delegate* unmanaged<int, long>)At(SupportedBuild.InventoryCount))(itemId);
    public void HandleLocalItemUse(nint bag, nint request) =>
        ((delegate* unmanaged<nint, nint, void>)At(SupportedBuild.LocalItemUse))(bag, request);

    public nint FindRolesProxy(nint bag)
    {
        var type = ((delegate* unmanaged<nint>)At(SupportedBuild.RolesClass))();
        return ((delegate* unmanaged<nint, nint, nint>)At(SupportedBuild.FindProxy))(bag, type);
    }

    public void NotifyRoleItemUse(nint roles, nint request)
    {
        RoleUseEvent notification = default;
        notification.ItemId = ItemId(request);
        Copy(&notification.Role, &((ItemRequest*)request)->Role);
        try
        {
            ((delegate* unmanaged<nint, void*, void>)At(SupportedBuild.Broadcast))(
                roles + SupportedBuild.RolesItemUseDelegate, &notification);
        }
        finally { Free(&notification.Role); }
    }

    public void AddAffinity(nint bag, nint request, int amount) =>
        ((delegate* unmanaged<nint, nint, int, byte, nint, void>)At(SupportedBuild.AddAffinity))(
            bag, request, amount, 1, At(SupportedBuild.UseItemReason));

    public void ItemUseFailed(string message)
    {
        string text = "[Dollhouse items] " + message;
        try
        {
            fixed (char* chars = text)
                ((delegate* unmanaged<char*, void>)At(SupportedBuild.GameplayLog))(chars);
        }
        catch (Exception) { Console.Error.WriteLine(text); }
    }

    public void NotifyItemUseUI(nint bag, nint request)
    {
        ItemRequest notification = *(ItemRequest*)request;
        notification.Role = default;
        Copy(&notification.Role, &((ItemRequest*)request)->Role);
        try
        {
            ((delegate* unmanaged<nint, void*, void>)At(SupportedBuild.Broadcast))(
                bag + SupportedBuild.BagItemUseUIDelegate, &notification);
        }
        finally { Free(&notification.Role); }
    }
}
