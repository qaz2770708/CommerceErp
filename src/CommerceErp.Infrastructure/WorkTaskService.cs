using System.Security.Cryptography;
using System.Text;
using CommerceErp.Core;
using static CommerceErp.Infrastructure.CommerceDatabase;

namespace CommerceErp.Infrastructure;

public sealed class WorkTaskService(CommerceDatabase database)
{
    private static bool Allows(string permission)=>SecurityContext.Current is null||AccountPermissions.Allows(SecurityContext.Current.User,permission);
    private static bool RetryAllowed(WorkTaskKind kind,string related,string state)=>
        (state is "Blocked" or "Failed" or "Rejected" or "RetryWait" or "Unknown")&&Allows(kind==WorkTaskKind.ShipmentDelivery?"warehouse.write":related.StartsWith("stock:",StringComparison.Ordinal)?"inventory.write":"orders.write");
    public static void CheckAccess(DbSession db,AccountInfo user,WorkTaskKind kind,string id,bool retry)
    {
        if(!Enum.IsDefined(kind))throw new BusinessRuleException("任务类型无效。");
        if(kind==WorkTaskKind.ShipmentDelivery)
        {
            if(!AccountPermissions.Allows(user,retry?"warehouse.write":"warehouse.read"))throw new AccessDeniedException();
            var shipment=db.Scalar<string>("SELECT ShipmentId FROM DeliveryJobs WHERE Id=$id",("id",id));if(shipment is null)throw new AccessDeniedException();
            DataAccessScope.Shipment(db,user,shipment);return;
        }
        var row=db.Query("SELECT TenantId,AggregateId FROM Outbox WHERE Id=$id",r=>(Tenant:r.GetString(0),Aggregate:r.GetString(1)),("id",id)).SingleOrDefault();
        if(row.Tenant!=user.TenantId)throw new AccessDeniedException();
        if(row.Aggregate.StartsWith("stock:",StringComparison.Ordinal))
        {if(!AccountPermissions.Allows(user,retry?"inventory.write":"inventory.read")||!user.Warehouses.Contains(WarehouseOf(db)))throw new AccessDeniedException();}
        else {DataAccessScope.Order(db,user,row.Aggregate);if(retry&&!AccountPermissions.Allows(user,"orders.write"))throw new AccessDeniedException();}
    }
    private static string SourceSql()
    {
        var filter=SecurityContext.Current is null?"":" AND (EXISTS(SELECT 1 FROM SalesOrders scoped_order WHERE scoped_order.Id=e.AggregateId AND scoped_order.TenantId=e.TenantId"+SecurityContext.ShopFilter("scoped_order")+")"+(Allows("inventory.read")?" OR e.AggregateId LIKE 'stock:%'":"")+")";
        return """
            SELECT 'BusinessEvent' Kind,e.Id,e.EventType Title,e.AggregateId RelatedId,COALESCE(o.ExternalOrderId,e.AggregateId) RelatedNumber,COALESCE(o.ShopId,'') ShopId,
            e.State,e.Version,e.Attempts,e.LastCode,e.CreatedAt,e.UpdatedAt,e.NextAttemptAt
            FROM Outbox e LEFT JOIN SalesOrders o ON o.Id=e.AggregateId AND o.TenantId=e.TenantId WHERE e.TenantId=$tenant
            """+filter+" UNION ALL "+"""
            SELECT 'ShipmentDelivery',j.Id,'ShipmentDelivery',j.OrderId,o.ExternalOrderId,o.ShopId,j.State,j.Version,j.Attempts,
            CASE WHEN j.State='Blocked' THEN 'NotConfigured' ELSE j.State END,j.CreatedAt,j.UpdatedAt,j.NextAttemptAt
            FROM DeliveryJobs j JOIN SalesOrders o ON o.Id=j.OrderId WHERE o.TenantId=$tenant
            """+(Allows("warehouse.read")?SecurityContext.ShopFilter("o"):" AND 1=0");
    }
    private static string Message(string code,string state)=>code switch
    {
        "NotConfigured"=>"未配置已授权的连接器，尚未向平台发送。",
        "HandlerConflict"=>"处理器配置冲突，需要管理员处理。",
        "RetryLimit"=>"自动尝试次数已用尽，请核查结果后决定是否重试。",
        "CallInterrupted"=>"调用中断，结果尚未确认；再次处理时先回查。",
        _=>state switch{"Pending"=>"等待后台处理；同一业务对象的前序事件须先完成。","Processing"=>"后台处理中，中断后可在执行租约到期时接续。","Confirmed"=>"连接器已返回可核验回执。","Blocked"=>"处理条件未满足。","RetryWait"=>"暂时无法处理，已安排延迟重试。","Unknown"=>"外部结果不明，需回查确认。","Failed" or "Rejected"=>"处理被拒绝或尝试次数耗尽，请核查。",_=>"请核查任务状态。"}
    };
    private static WorkTaskRow Row(DbRow r)
    {
        var kind=Enum.Parse<WorkTaskKind>(r.GetString(0));var related=r.GetString(3);var state=r.GetString(6);
        return new(kind,r.GetString(1),r.GetString(2),related,r.GetString(4),r.GetString(5),state,r.GetInt64(7),r.GetInt32(8),Message(r.GetString(9),state),r.GetString(10),r.GetString(11),r.IsDBNull(12)?null:r.GetString(12),RetryAllowed(kind,related,state));
    }
    private static void QueryCheck(int page,int pageSize,string search,string state,string kind)
    {
        if(page is <1 or >1000000||pageSize is <1 or >100||search is null||search.Length>150||state is null||kind is null)throw new BusinessRuleException("任务查询参数无效。");
        if(state!=""&&!new[]{"Pending","Processing","RetryWait","Unknown","Blocked","Failed","Rejected","Confirmed"}.Contains(state))throw new BusinessRuleException("任务状态无效。");
        if(kind!=""&&!Enum.GetNames<WorkTaskKind>().Contains(kind))throw new BusinessRuleException("任务类型无效。");
    }
    public WorkTaskPage List(int page=1,int pageSize=20,string search="",string state="",string kind="")
    {
        QueryCheck(page,pageSize,search,state,kind);
        return database.Read(db=>
        {
            var query=" FROM ("+SourceSql()+") tasks WHERE ($state='' OR State=$state) AND ($kind='' OR Kind=$kind) AND (CHARINDEX($q,RelatedNumber)>0 OR CHARINDEX($q,Id)>0)";
            (string Name,object? Value)[] args=[("tenant",Tenant(db)),("state",state),("kind",kind),("q",search.Trim())];
            var total=db.Scalar<int>("SELECT COUNT(*)"+query,args);
            var items=db.Query("SELECT *"+query+" ORDER BY CreatedAt DESC,Id,Kind"+db.Page,Row,[..args,("offset",(page-1)*pageSize),("limit",pageSize)]);
            return new WorkTaskPage(items,total,page,pageSize);
        });
    }
    public WorkTaskSummary Summary()=>database.Read(db=>
    {
        var rows=db.Query("SELECT State,COUNT(*),MIN(CreatedAt) FROM ("+SourceSql()+") tasks GROUP BY State",r=>(State:r.GetString(0),Count:r.GetInt32(1),Oldest:r.GetString(2)),("tenant",Tenant(db)));
        int Count(params string[] states)=>rows.Where(r=>states.Contains(r.State)).Sum(r=>r.Count);
        return new WorkTaskSummary(rows.Sum(r=>r.Count),Count("Pending","RetryWait"),Count("Processing"),Count("Unknown","Blocked","Failed","Rejected"),Count("Confirmed"),rows.Where(r=>r.State is "Pending" or "RetryWait").Select(r=>r.Oldest).Order(StringComparer.Ordinal).FirstOrDefault());
    });
    private static WorkTaskRow Read(DbSession db,WorkTaskKind kind,string id)=>db.Query("SELECT * FROM ("+SourceSql()+") tasks WHERE Kind=$kind AND Id=$id",Row,("tenant",Tenant(db)),("kind",kind.ToString()),("id",id)).SingleOrDefault()??throw new AccessDeniedException();
    public WorkTaskDetails Details(WorkTaskKind kind,string id)=>database.Read(db=>
    {
        var row=Read(db,kind,id);List<WorkTaskHistory> history;
        if(kind==WorkTaskKind.BusinessEvent)
        {
            history=db.Query("SELECT Outcome,StartedAt,FinishedAt FROM OutboxAttempts WHERE TaskId=$id ORDER BY StartedAt DESC,LeaseToken"+db.Limit(100),r=>new WorkTaskHistory(r.GetString(0),"后台服务","",r.GetString(1),r.IsDBNull(2)?null:r.GetString(2)),("id",id));
            history.AddRange(db.Query("SELECT Actor,Reason,CreatedAt FROM TaskActions WHERE TaskId=$id ORDER BY CreatedAt DESC,Id"+db.Limit(100),r=>new WorkTaskHistory("ManualRetry",r.GetString(0),PersonalDataView.Allowed?r.GetString(1):"[已隐藏]",r.GetString(2),r.GetString(2)),("id",id)));
        }
        else
        {
            history=db.Query("SELECT Outcome,CreatedAt FROM DeliveryAttempts WHERE JobId=$id ORDER BY CreatedAt DESC,Id"+db.Limit(100),r=>new WorkTaskHistory(r.GetString(0),"后台服务","",r.GetString(1),r.GetString(1)),("id",id));
            history.AddRange(db.Query("SELECT h.Actor,h.Reason,h.CreatedAt FROM ShipmentHistory h JOIN DeliveryJobs j ON j.ShipmentId=h.ShipmentId WHERE j.Id=$id AND h.Action='RetryDelivery' AND CHARINDEX($id,h.DataJson)>0 ORDER BY h.Id DESC"+db.Limit(100),r=>new WorkTaskHistory("ManualRetry",r.GetString(0),PersonalDataView.Allowed?r.GetString(1):"[已隐藏]",r.GetString(2),r.GetString(2)),("id",id)));
        }
        return new WorkTaskDetails(row,history.OrderByDescending(h=>h.StartedAt,StringComparer.Ordinal).Take(100).ToList());
    });
    public WorkTaskResult Retry(WorkTaskKind kind,string id,RetryWorkTaskRequest request)
    {
        if(!Enum.IsDefined(kind)||!Guid.TryParse(request.OperationId,out var op)||op==Guid.Empty||string.IsNullOrWhiteSpace(request.Reason)||request.Reason.Length>500||request.Reason.Any(char.IsControl))throw new BusinessRuleException("重试须提供有效操作号和 1–500 字原因。");
        var actor=SecurityContext.Current is{} current?AccountService.Actor(current.User):request.Actor;
        if(string.IsNullOrWhiteSpace(actor)||actor.Length>100)throw new BusinessRuleException("操作人无效。");
        if(kind==WorkTaskKind.ShipmentDelivery)
        {
            // Existing delivery command owns idempotency, same-transaction audit and shipment history.
            var r=new OrderService(database).RetryDelivery(id,new(request.OperationId,request.ExpectedVersion,actor,request.Reason));
            return new(r.Id,r.Version,r.Replayed,r.Message);
        }
        var hash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Json(new{id,request=request with{Actor=actor}}))));
        return database.Write(db=>
        {
            var replay=db.Query("SELECT Kind,RequestHash,ResponseJson FROM Operations WHERE TenantId=$tenant AND OperationId=$op",r=>(Kind:r.GetString(0),Hash:r.GetString(1),Json:r.GetString(2)),("tenant",Tenant(db)),("op",request.OperationId)).SingleOrDefault();
            if(replay.Json is not null)
            {if(replay.Kind!="RetryOutbox"||replay.Hash!=hash)throw new ConflictException("操作号已用于不同请求。");return FromJson<WorkTaskResult>(replay.Json) with{Replayed=true};}
            var row=Read(db,kind,id);
            if(row.Version!=request.ExpectedVersion)throw new ConflictException("任务已变化，请刷新后重试。");
            if(!row.CanRetry)throw new ConflictException("该任务正在排队、处理中或已确认，不能重复加入队列。");
            var now=DateTimeOffset.UtcNow.ToString("O");
            db.Execute("UPDATE Outbox SET State='Pending',Version=Version+1,CycleAttempts=0,NextAttemptAt=$now,LeaseToken=NULL,LeaseUntil=NULL,LastCode='',UpdatedAt=$now WHERE Id=$id",("now",now),("id",id));
            db.Execute("INSERT INTO TaskActions VALUES($op,$id,$actor,$reason,$now)",("op",request.OperationId),("id",id),("actor",actor),("reason",request.Reason.Trim()),("now",now));
            var result=new WorkTaskResult(id,row.Version+1,false,"已重新排队；后台先回查处理结果，业务单据和库存未被重做。");
            db.Execute("INSERT INTO Operations VALUES($tenant,$op,'RetryOutbox',$hash,$json,$now)",("tenant",Tenant(db)),("op",request.OperationId),("hash",hash),("json",Json(result)),("now",now));
            return result;
        });
    }
}
