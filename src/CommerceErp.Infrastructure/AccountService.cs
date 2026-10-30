using System.Security.Cryptography;
using System.Text;
using CommerceErp.Core;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using static CommerceErp.Infrastructure.CommerceDatabase;

namespace CommerceErp.Infrastructure;

public sealed partial class AccountService(CommerceDatabase database)
{
    private static readonly PasswordHasher<string> Hasher = new(Options.Create(new PasswordHasherOptions { IterationCount = 210000 }));
    private static readonly string DummyHash = Hasher.HashPassword("dummy", Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
    public static string TokenHash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    public static string Actor(AccountInfo user) => user.Username + " (" + user.Id + ")";
    private static string Username(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !System.Text.RegularExpressions.Regex.IsMatch(value, "^[a-zA-Z0-9._-]{3,32}$"))
            throw new BusinessRuleException("账号名为 3–32 位英文字母、数字、点、横线或下划线。");
        return value.ToLowerInvariant();
    }
    private static void Password(string value)
    {
        if (value is null || value.Length is < 12 or > 128 || value.Any(char.IsControl)) throw new BusinessRuleException("密码须为 12–128 个非控制字符。");
    }
    private static void Text(string? value, string field, int max)
    { if (string.IsNullOrWhiteSpace(value) || value.Length>max || value.Any(char.IsControl)) throw new BusinessRuleException(field+"不能为空或超过长度限制。"); }
    internal static AccountInfo? User(DbSession db, string id)
    {
        var user = db.Query("SELECT Id,Username,DisplayName,Role,TenantId,Enabled,Version,MustChangePassword FROM SecurityUsers WHERE Id=$id", r => new AccountInfo(r.GetString(0),r.GetString(1),r.GetString(2),Enum.Parse<AccountRole>(r.GetString(3)),r.GetString(4),r.GetInt64(5)==1,r.GetInt64(6),r.GetInt64(7)==1,[],[]), ("id",id)).SingleOrDefault();
        if (user is null) return null;
        return user with { Shops = db.Query("SELECT ShopId FROM SecurityUserShops WHERE UserId=$id ORDER BY ShopId",r=>r.GetString(0),("id",id)), Warehouses = db.Query("SELECT WarehouseId FROM SecurityUserWarehouses WHERE UserId=$id ORDER BY WarehouseId",r=>r.GetString(0),("id",id)) };
    }
    internal static AccountInfo Session(DbSession db, string hash)
    {
        var session=db.Query("SELECT UserId,UserVersion FROM SecuritySessions WHERE TokenHash=$hash AND Revoked=0 AND ExpiresAt>$now",r=>(Id:r.GetString(0),Version:r.GetInt64(1)),("hash",hash),("now",Now())).SingleOrDefault();
        var user=session.Id is null?null:User(db,session.Id);
        if(user is null||!user.Enabled||user.Version!=session.Version)throw new AuthenticationException();return user;
    }
    internal static void Audit(DbSession db,string actorId,string actorName,string action,string resource,string outcome,string operation="",string reason="") => db.Execute(
        "INSERT INTO SecurityAudit(ActorId,ActorName,Action,Resource,Outcome,OperationId,CreatedAt,Reason) VALUES($id,$name,$action,$resource,$outcome,$op,$now,$reason)",
        ("id",actorId),("name",actorName),("action",action),("resource",resource),("outcome",outcome),("op",operation),("now",Now()),("reason",reason));
    public SecurityStatus Status()=>database.Read(db=>new SecurityStatus(db.Scalar<int>("SELECT COUNT(*) FROM SecurityUsers")==0));
    public AccountInfo Setup(SetupAccountRequest request)
    {
        var username=Username(request.Username);Text(request.DisplayName,"显示名称",50);Password(request.Password);
        var hash=Hasher.HashPassword(username,request.Password);
        return database.Write(db=>
        {
            if(db.Scalar<int>("SELECT COUNT(*) FROM SecurityUsers")!=0)throw new ConflictException("管理员已建立，初始化入口已关闭。");
            var id=Guid.NewGuid().ToString("N");
            Insert(db,id,username,request.DisplayName,AccountRole.Administrator,hash,false);
            Scopes(db,id,ShopIds(db),[WarehouseOf(db)]);
            Audit(db,id,username,"SetupAdministrator",id,"Committed");return User(db,id)!;
        });
    }
    public LoginResult Login(LoginRequest request)
    {
        var username=Username(request.Username);
        if(request.Password is null||request.Password.Length>128)throw new AuthenticationException("账号或密码错误，或账号暂时不可用。");
        var attempt=database.Write(db=>
        {
            var row=db.Query("SELECT Id,PasswordHash,Enabled,FailedAttempts,LockedUntil FROM SecurityUsers WHERE Username=$name",r=>(Id:r.GetString(0),Hash:r.GetString(1),Enabled:r.GetInt64(2)==1,Failed:r.GetInt32(3),Until:r.IsDBNull(4)?null:r.GetString(4)),("name",username)).SingleOrDefault();
            var verified=Hasher.VerifyHashedPassword(username,row.Hash??DummyHash,request.Password)!=PasswordVerificationResult.Failed;
            if(row.Id is null||!row.Enabled||row.Until is not null&&DateTimeOffset.Parse(row.Until)>DateTimeOffset.UtcNow||!verified)
            {
                if(row.Id is not null && (row.Until is null || DateTimeOffset.Parse(row.Until)<=DateTimeOffset.UtcNow))
                {
                    var failures=row.Until is not null?1:row.Failed+1;
                    db.Execute("UPDATE SecurityUsers SET FailedAttempts=$count,LockedUntil=$until WHERE Id=$id",("count",failures),("until",failures>=5?DateTimeOffset.UtcNow.AddMinutes(15).ToString("O"):null),("id",row.Id));
                }
                Audit(db,row.Id??"",username,"Login","session","Denied");return (LoginResult?)null;
            }
            db.Execute("UPDATE SecurityUsers SET FailedAttempts=0,LockedUntil=NULL WHERE Id=$id",("id",row.Id));
            db.Execute("DELETE FROM SecuritySessions WHERE ExpiresAt<$now OR Revoked=1",("now",Now()));
            var user=User(db,row.Id)!;var token=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));var expires=DateTimeOffset.UtcNow.AddHours(8);
            db.Execute("INSERT INTO SecuritySessions VALUES($hash,$id,$version,$expires,0)",("hash",TokenHash(token)),("id",user.Id),("version",user.Version),("expires",expires.ToString("O")));
            Audit(db,user.Id,user.Username,"Login","session","Committed");return new LoginResult(token,expires,user);
        });
        return attempt??throw new AuthenticationException("账号或密码错误，或账号暂时不可用。");
    }
    public AccountInfo Authenticate(string token)=>database.Read(db=>Session(db,TokenHash(token)));
    public List<AccountInfo> Accounts()=>database.Read(db=>db.Query("SELECT Id FROM SecurityUsers ORDER BY Username",r=>r.GetString(0)).Select(id=>User(db,id)!).ToList());
    public AccountInfo Save(string? id,SaveAccountRequest request)
    {
        var username=Username(request.Username);Text(request.DisplayName,"显示名称",50);Text(request.Reason,"修改原因",500);
        string? hash=null;
        if(id is null){Password(request.InitialPassword!);hash=Hasher.HashPassword(username,request.InitialPassword!);}
        else if(request.InitialPassword is not null)throw new BusinessRuleException("修改账号不能夹带密码，请使用重置密码入口。");
        return database.Write(db=>
        {
            var shops=ShopIds(db);
            if(!Enum.IsDefined(request.Role)||request.TenantId!=Tenant(db)||request.Shops is null||request.Warehouses is null||request.Shops.Distinct().Count()!=request.Shops.Count||request.Shops.Any(s=>!shops.Contains(s))||request.Warehouses.Count!=1||request.Warehouses[0]!=WarehouseOf(db)||request.Role!=AccountRole.Administrator&&request.Shops.Count==0)throw new BusinessRuleException("请选择当前企业、已登记店铺及仓库。");
            if(request.Role==AccountRole.Administrator&&!shops.All(request.Shops.Contains))throw new BusinessRuleException("管理员须覆盖全部店铺。");

            if(id is null)
            {
                if(request.ExpectedVersion!=0)throw new ConflictException("新账号版本应为 0。");
                if(db.Scalar<int>("SELECT COUNT(*) FROM SecurityUsers WHERE Username=$name",("name",username))!=0)throw new ConflictException("账号名已存在。");
                id=Guid.NewGuid().ToString("N");Insert(db,id,username,request.DisplayName,request.Role,hash!,true);
                if(!request.Enabled)db.Execute("UPDATE SecurityUsers SET Enabled=0 WHERE Id=$id",("id",id));
            }
            else
            {
                var current=User(db,id)??throw new BusinessRuleException("账号不存在。");
                if(current.Username!=username||current.Version!=request.ExpectedVersion)throw new ConflictException("账号名不能修改，或版本已变化。");
                if(current.Role==AccountRole.Administrator&&current.Enabled&&(!request.Enabled||request.Role!=AccountRole.Administrator)&&db.Scalar<int>("SELECT COUNT(*) FROM SecurityUsers WHERE Role='Administrator' AND Enabled=1")<=1)
                    throw new ConflictException("不能停用或降级最后一个有效管理员。");
                db.Execute("UPDATE SecurityUsers SET DisplayName=$name,Role=$role,Enabled=$enabled,Version=Version+1 WHERE Id=$id",("name",request.DisplayName.Trim()),("role",request.Role.ToString()),("enabled",request.Enabled?1:0),("id",id));
                db.Execute("UPDATE SecuritySessions SET Revoked=1 WHERE UserId=$id",("id",id));
            }
            Scopes(db,id,request.Shops,request.Warehouses);Audit(db,SecurityContext.Current?.User.Id??"",SecurityContext.Current?.User.Username??"","SaveAccount",id,"Committed",reason:request.Reason.Trim());return User(db,id)!;
        });
    }
    public void ResetPassword(string id,ResetPasswordRequest request)
    {
        Password(request.NewPassword);Text(request.Reason,"重置原因",500);var hash=Hasher.HashPassword(id,request.NewPassword);
        database.Write(db=>{var user=User(db,id)??throw new BusinessRuleException("账号不存在。");if(user.Version!=request.ExpectedVersion)throw new ConflictException("账号版本已变化。");
            db.Execute("UPDATE SecurityUsers SET PasswordHash=$hash,MustChangePassword=1,Version=Version+1,FailedAttempts=0,LockedUntil=NULL WHERE Id=$id",("hash",hash),("id",id));
            db.Execute("UPDATE SecuritySessions SET Revoked=1 WHERE UserId=$id",("id",id));Audit(db,SecurityContext.Current?.User.Id??"",SecurityContext.Current?.User.Username??"","ResetPassword",id,"Committed",reason:request.Reason.Trim());return true;});
    }
    public void ChangePassword(string token,ChangePasswordRequest request)
    {
        Password(request.NewPassword);Password(request.CurrentPassword);if(request.NewPassword==request.CurrentPassword)throw new BusinessRuleException("新密码不能与旧密码相同。");
        database.Write(db=>{var user=Session(db,TokenHash(token));var hash=db.Scalar<string>("SELECT PasswordHash FROM SecurityUsers WHERE Id=$id",("id",user.Id));
            if(Hasher.VerifyHashedPassword(user.Username,hash,request.CurrentPassword)==PasswordVerificationResult.Failed)throw new AuthenticationException("当前密码不正确。");
            db.Execute("UPDATE SecurityUsers SET PasswordHash=$hash,MustChangePassword=0,Version=Version+1 WHERE Id=$id",("hash",Hasher.HashPassword(user.Username,request.NewPassword)),("id",user.Id));
            db.Execute("UPDATE SecuritySessions SET Revoked=1 WHERE UserId=$id",("id",user.Id));return true;});
    }
    public void Logout(string token)=>database.Write(db=>{db.Execute("UPDATE SecuritySessions SET Revoked=1 WHERE TokenHash=$hash",("hash",TokenHash(token)));return true;});
    public List<SecurityAudit> AuditLog()=>database.Read(db=>db.Query("SELECT Id,ActorId,ActorName,Action,Resource,Outcome,OperationId,CreatedAt,Reason FROM SecurityAudit ORDER BY Id DESC"+db.Limit(200),r=>new SecurityAudit(r.GetInt64(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetString(4),r.GetString(5),r.GetString(6),r.GetString(7),r.GetString(8))));
    public void Denied(string actorId,string name,string path)=>database.Write(db=>{Audit(db,actorId,name,"AccessDenied",path,"Denied");return true;});
    private static void Insert(DbSession db,string id,string username,string name,AccountRole role,string hash,bool force)=>db.Execute("INSERT INTO SecurityUsers(Id,Username,DisplayName,Role,TenantId,Enabled,Version,MustChangePassword,PasswordHash,CreatedAt) VALUES($id,$username,$name,$role,$tenant,1,1,$force,$hash,$now)",("id",id),("username",username),("name",name.Trim()),("role",role.ToString()),("tenant",Tenant(db)),("force",force?1:0),("hash",hash),("now",Now()));
    private static void Scopes(DbSession db,string id,List<string> shops,List<string> warehouses)
    {
        db.Execute("DELETE FROM SecurityUserShops WHERE UserId=$id",("id",id));db.Execute("DELETE FROM SecurityUserWarehouses WHERE UserId=$id",("id",id));
        foreach(var shop in shops)db.Execute("INSERT INTO SecurityUserShops VALUES($id,$shop)",("id",id),("shop",shop));
        foreach(var warehouse in warehouses)db.Execute("INSERT INTO SecurityUserWarehouses VALUES($id,$warehouse)",("id",id),("warehouse",warehouse));
    }
}
