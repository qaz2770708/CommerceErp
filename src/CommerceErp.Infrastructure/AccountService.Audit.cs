using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CommerceErp.Core;
using static CommerceErp.Infrastructure.CommerceDatabase;

namespace CommerceErp.Infrastructure;

public sealed partial class AccountService
{
    private const string AuditColumns="Id,ActorId,ActorName,Action,Resource,Outcome,OperationId,CreatedAt,Reason";
    private static SecurityAudit AuditRow(DbRow r)=>new(r.GetInt64(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetString(4),r.GetString(5),r.GetString(6),r.GetString(7),r.GetString(8));
    private static (string Where,(string Name,object? Value)[] Args,long Snapshot) AuditFilter(DbSession db,AuditQuery q)
    {
        if(q is null || q.Page is <1 or >1000000 || q.PageSize is <1 or >200 || q.SnapshotId<0 ||
           new[]{q.Actor,q.Action,q.Outcome,q.Resource}.Any(s=>s is null||s.Length>150||s.Any(char.IsControl)))throw new BusinessRuleException("审计分页或筛选参数无效。");
        if(q.Outcome!="" && q.Outcome is not("Committed" or "Denied"))throw new BusinessRuleException("结果须为 Committed 或 Denied。");
        string? Time(string? value)
        {
            if(string.IsNullOrEmpty(value))return null;
            if(!DateTimeOffset.TryParseExact(value,"O",CultureInfo.InvariantCulture,DateTimeStyles.None,out var date))throw new BusinessRuleException("审计时间须使用带时区的 ISO 8601 O 格式。");
            return date.ToUniversalTime().ToString("O");
        }
        var from=Time(q.From);var to=Time(q.To);if(from is not null&&to is not null&&string.CompareOrdinal(from,to)>0)throw new BusinessRuleException("起始时间不能晚于截止时间。");
        var maximum=db.Scalar<long>("SELECT COALESCE(MAX(Id),0) FROM SecurityAudit");var snapshot=Math.Min(q.SnapshotId??maximum,maximum);
        return("Id<=$snapshot AND ($actor='' OR CHARINDEX($actor,ActorName)>0 OR ActorId=$actor) AND ($action='' OR Action=$action) AND ($outcome='' OR Outcome=$outcome) AND ($resource='' OR CHARINDEX($resource,Resource)>0 OR OperationId=$resource) AND ($from IS NULL OR CreatedAt>=$from) AND ($to IS NULL OR CreatedAt<=$to)",
            [("snapshot",snapshot),("actor",q.Actor.Trim()),("action",q.Action.Trim()),("outcome",q.Outcome),("resource",q.Resource.Trim()),("from",from),("to",to)],snapshot);
    }
    public AuditPage QueryAudit(AuditQuery query)=>database.Read(db=>
    {
        var f=AuditFilter(db,query);var total=db.Scalar<long>("SELECT COUNT(*) FROM SecurityAudit WHERE "+f.Where,f.Args);
        var rows=db.Query("SELECT "+AuditColumns+" FROM SecurityAudit WHERE "+f.Where+" ORDER BY Id DESC"+db.Page,AuditRow,[..f.Args,("limit",query.PageSize),("offset",(query.Page-1)*query.PageSize)]);
        return new AuditPage(rows,total,query.Page,query.PageSize,f.Snapshot);
    });
    public AuditExportDocument ExportAudit(AuditExportRequest request)
    {
        Text(request.Reason,"导出原因",500);
        return database.Write(db=>
        {
            var f=AuditFilter(db,request.Query);
            var rows=db.Query("SELECT "+AuditColumns+" FROM SecurityAudit WHERE "+f.Where+" ORDER BY Id DESC"+db.Limit(10001),AuditRow,f.Args);
            if(rows.Count>10000)throw new BusinessRuleException("最多导出 10000 条，请缩小时间或其他筛选范围。");
            var at=Now();var json=Json(new{schema="commerce-audit/v1",createdAt=at,snapshotId=f.Snapshot,query=request.Query with{SnapshotId=f.Snapshot},rows});
            var hash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
            Audit(db,SecurityContext.Current?.User.Id??"local-tool",SecurityContext.Current?.User.Username??"local-tool","ExportAudit","SecurityAudit","Committed",reason:request.Reason.Trim()+" | rows="+rows.Count+" | sha256="+hash);
            return new AuditExportDocument(json,hash,rows.Count,f.Snapshot,at);
        });
    }
    public AccountInfo RecoverAdministrator(string username,string password,string reason,string expectedInstance)
    {
        if(SecurityContext.Current is not null)throw new AccessDeniedException("管理员恢复只允许本机受信工具执行。");
        username=Username(username);Password(password);Text(reason,"恢复原因",500);
        var hash=Hasher.HashPassword(username,password);
        return database.Write(db=>
        {
            if(!Guid.TryParseExact(expectedInstance,"N",out _)||db.Scalar<string>("SELECT Value FROM SystemMetadata WHERE Name='InstanceId'")!=expectedInstance)throw new ConflictException("数据库身份不符，恢复未执行。");
            var id=db.Scalar<string>("SELECT Id FROM SecurityUsers WHERE Username=$name AND Role='Administrator'",("name",username));
            if(id is null)throw new BusinessRuleException("只能恢复已存在的管理员账号，不能将普通账号提升为管理员。");
            db.Execute("UPDATE SecurityUsers SET PasswordHash=$hash,Enabled=1,MustChangePassword=1,FailedAttempts=0,LockedUntil=NULL,Version=Version+1 WHERE Id=$id",("hash",hash),("id",id));
            // Recovery is a break-glass action: all old sessions are invalidated.
            db.Execute("UPDATE SecuritySessions SET Revoked=1");
            Scopes(db,id,ShopIds(db),[WarehouseOf(db)]);
            Audit(db,"local-recovery",Environment.UserDomainName+"\\"+Environment.UserName,"RecoverAdministrator",id,"Committed",reason:reason.Trim());
            return User(db,id)!;
        });
    }
}
