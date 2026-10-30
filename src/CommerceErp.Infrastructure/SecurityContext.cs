using CommerceErp.Core;
namespace CommerceErp.Infrastructure;

public sealed class SecurityRequest(string tokenHash, AccountInfo user, string permission, string resource, string method,
    string operationId, bool fullScope, Action<DbSession,AccountInfo> resourceCheck)
{
    public string TokenHash { get; } = tokenHash;
    public AccountInfo User { get; set; } = user;
    public string Permission { get; } = permission;
    public string Resource { get; } = resource;
    public string Method { get; } = method;
    public string OperationId { get; } = operationId;
    public bool FullScope { get; } = fullScope;
    public Action<DbSession,AccountInfo> ResourceCheck { get; } = resourceCheck;
}
public static class SecurityContext
{
    private static readonly AsyncLocal<SecurityRequest?> Slot = new();
    public static SecurityRequest? Current { get => Slot.Value; set => Slot.Value=value; }
    public static string ShopFilter(string alias) => Current is null?"":" AND EXISTS(SELECT 1 FROM SecurityUserShops access_scope WHERE access_scope.UserId=$accessUser AND access_scope.ShopId="+alias+".ShopId)";
    public static T Run<T>(DbSession db,bool read,Func<DbSession,T> action)
    {
        var request=Current;
        if(request is null)return action(db); // In-process jobs/tools are trusted. All HTTP business endpoints set this context.
        var user=AccountService.Session(db,request.TokenHash);request.User=user;
        if(user.TenantId!=db.TenantId||!user.Warehouses.Contains(db.WarehouseId)||!AccountPermissions.Allows(user,request.Permission)||
            request.FullScope&&!CommerceDatabase.ShopIds(db).All(user.Shops.Contains))throw new AccessDeniedException();
        request.ResourceCheck(db,user);
        if(!read&&request.OperationId.Length>0)
        {
            var owner=db.Scalar<string>("SELECT UserId FROM SecurityOperationOwners WHERE OperationId=$op",("op",request.OperationId));
            if(owner is not null&&owner!=user.Id)throw new AccessDeniedException("此操作标识属于其他账号，不能跨账号重试。");
            if(owner is null&&user.Role!=AccountRole.Administrator&&db.Scalar<int>("SELECT COUNT(*) FROM Operations WHERE OperationId=$op",("op",request.OperationId))>0)
                throw new AccessDeniedException("历史操作尚未绑定账号，须由管理员核对。");
        }
        var result=action(db);
        if(!read)
        {
            if(request.OperationId.Length>0&&db.Scalar<int>("SELECT COUNT(*) FROM SecurityOperationOwners WHERE OperationId=$op",("op",request.OperationId))==0)
                db.Execute("INSERT INTO SecurityOperationOwners VALUES($op,$id)",("op",request.OperationId),("id",user.Id));
            AccountService.Audit(db,user.Id,user.Username,request.Method,request.Resource,"Committed",request.OperationId);
        }
        return result;
    }
}
