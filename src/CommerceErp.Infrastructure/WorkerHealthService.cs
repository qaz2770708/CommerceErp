using CommerceErp.Core;
using static CommerceErp.Infrastructure.CommerceDatabase;
namespace CommerceErp.Infrastructure;
public sealed class WorkerHealthService(CommerceDatabase database)
{
    public void Pulse(string name,string error="")=>database.Write(db=>
    {
        var now=Now();db.Upsert("UPDATE WorkerHealth SET LastSeen=$now,LastSuccess=CASE WHEN $error='' THEN $now ELSE LastSuccess END,LastError=$error,ConsecutiveErrors=CASE WHEN $error='' THEN 0 ELSE ConsecutiveErrors+1 END WHERE Name=$name","INSERT INTO WorkerHealth VALUES($name,$now,$success,$error,$count)",("name",name),("now",now),("success",error==""?now:""),("error",error),("count",error==""?0:1));return true;
    });
    public TaskHealth Read()=>database.Read<TaskHealth>(db=>
    {
        var now=DateTimeOffset.UtcNow;var workers=db.Query("SELECT Name,LastSeen,LastSuccess,LastError,ConsecutiveErrors FROM WorkerHealth WHERE LastSeen>$cutoff ORDER BY Name",r=>new WorkerHealth(r.GetString(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetInt32(4)),("cutoff",now.AddDays(-1).ToString("O")));
        var expired=db.Scalar<int>("SELECT COUNT(*) FROM Outbox WHERE State='Processing' AND LeaseUntil<$now",("now",now.ToString("O")))+db.Scalar<int>("SELECT COUNT(*) FROM DeliveryJobs WHERE State='Processing' AND LeaseUntil<$now",("now",now.ToString("O")));
        var exhausted=db.Scalar<int>("SELECT COUNT(*) FROM Outbox WHERE State IN('Unknown','Failed') AND CycleAttempts>=5 AND NextAttemptAt IS NULL")+db.Scalar<int>("SELECT COUNT(*) FROM DeliveryJobs WHERE State='Unknown' AND CycleAttempts>=5 AND NextAttemptAt IS NULL");
        var pending=db.Scalar<int>("SELECT COUNT(*) FROM Outbox WHERE State='Pending' AND CreatedAt<$old",("old",now.AddMinutes(-5).ToString("O")))+db.Scalar<int>("SELECT COUNT(*) FROM DeliveryJobs WHERE State='Pending' AND CreatedAt<$old",("old",now.AddMinutes(-5).ToString("O")));
        var unhealthy=workers.Count(w=>w.LastError!="Stopped"&&(w.ConsecutiveErrors>0||DateTimeOffset.Parse(w.LastSeen)<now.AddMinutes(-1)));
        return new(workers,expired,exhausted,pending,$"后台异常/失联 {unhealthy} · 租约过期 {expired} · 重试耗尽 {exhausted} · 等待超过5分钟 {pending}。心跳仅表示本地处理循环运行，不代表平台成功。");
    });
}
