namespace CommerceErp.Core;

public enum AccountRole { Administrator, CustomerService, Warehouse, Finance, Observer, OpeningClerk, OpeningApprover, OpeningPolicyManager, InventoryApprover }
public sealed record AccountInfo(string Id, string Username, string DisplayName, AccountRole Role, string TenantId,
    bool Enabled, long Version, bool MustChangePassword, List<string> Shops, List<string> Warehouses);
public sealed record LoginRequest(string Username, string Password);
public sealed record LoginResult(string Token, DateTimeOffset ExpiresAt, AccountInfo Account);
public sealed record SecurityStatus(bool SetupRequired);
public sealed record SetupAccountRequest(string Username, string DisplayName, string Password);
public sealed record SaveAccountRequest(string Username, string DisplayName, AccountRole Role, string TenantId,
    bool Enabled, long ExpectedVersion, List<string> Shops, List<string> Warehouses, string Reason, string? InitialPassword = null);
public sealed record ResetPasswordRequest(long ExpectedVersion, string NewPassword, string Reason);
public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);
public sealed record SecurityAudit(long Id, string ActorId, string ActorName, string Action, string Resource, string Outcome, string OperationId, string CreatedAt, string Reason);
public sealed record AuditQuery(int Page=1,int PageSize=50,string Actor="",string Action="",string Outcome="",string Resource="",string? From=null,string? To=null,long? SnapshotId=null);
public sealed record AuditPage(List<SecurityAudit> Items,long Total,int Page,int PageSize,long SnapshotId);
public sealed record AuditExportRequest(AuditQuery Query,string Reason);
public sealed record AuditExportDocument(string Json,string Sha256,int Rows,long SnapshotId,string CreatedAt);
public sealed class AuthenticationException(string message = "登录已失效，请重新登录。") : Exception(message);
public sealed class AccessDeniedException(string message = "没有执行此操作或访问该数据范围的权限。") : Exception(message);

public static class AccountPermissions
{
    public static bool Allows(AccountInfo account, string permission)
    {
        if (!account.Enabled) return false;
        if (permission == "self") return true;
        if (account.MustChangePassword) return false;
        if (account.Role == AccountRole.Administrator) return true;
        return permission switch
        {
            "stockdocs.read" => account.Role is AccountRole.Warehouse or AccountRole.Observer or AccountRole.InventoryApprover,
            "stockdocs.write" => account.Role==AccountRole.Warehouse,
            "stockdocs.approve" => account.Role==AccountRole.InventoryApprover,
            "master.read" or "opening.read" => account.Role is AccountRole.OpeningClerk or AccountRole.OpeningApprover or AccountRole.OpeningPolicyManager,
            "opening.write" => account.Role==AccountRole.OpeningClerk,
            "opening.approve" => account.Role==AccountRole.OpeningApprover,
            "opening.policy" => account.Role==AccountRole.OpeningPolicyManager,
            "tasks.read" => true,
            "tasks.retry" => account.Role is AccountRole.CustomerService or AccountRole.Warehouse,
            "personal.read" => account.Role is AccountRole.CustomerService or AccountRole.Warehouse,
            "orders.read" or "catalog.read" => true,
            "orders.write" or "aftersales.manage" => account.Role == AccountRole.CustomerService,
            "aftersales.read" => true,
            "warehouse.read" or "inventory.read" => account.Role is AccountRole.Warehouse or AccountRole.Observer or AccountRole.OpeningClerk or AccountRole.OpeningApprover or AccountRole.InventoryApprover,
            "warehouse.write" or "inventory.write" or "aftersales.inspect" => account.Role == AccountRole.Warehouse,
            "finance.read" => account.Role is AccountRole.Finance or AccountRole.Observer,
            "finance.write" or "aftersales.refund" => account.Role == AccountRole.Finance,
            _ => false
        };
    }
}
