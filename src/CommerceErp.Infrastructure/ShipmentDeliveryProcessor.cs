using CommerceErp.Core;
using static CommerceErp.Infrastructure.CommerceDatabase;

namespace CommerceErp.Infrastructure;

// No platform connector is registered in this delivery. Configured connectors must implement
// authoritative lookup and preserve DeliveryId as the stable external idempotency identity.
public sealed class ShipmentDeliveryProcessor(CommerceDatabase database, IEnumerable<IShipmentGateway> gateways, TimeProvider? timeProvider = null,TimeSpan? callTimeout=null)
{
    private readonly TimeSpan _callTimeout=callTimeout is{} value&&value>TimeSpan.Zero&&value<=TimeSpan.FromSeconds(35)?value:callTimeout is null?TimeSpan.FromSeconds(35):throw new ArgumentOutOfRangeException(nameof(callTimeout));
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly IShipmentGateway[] _gateways = gateways.ToArray();
    private sealed record Claim(string Id, string Lease, ShipmentNotification Payload,int Cycle);
    public async Task<bool> ProcessNextAsync(CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow();
        var claim = database.Write(db =>
        {
            var record = db.Query("""
                SELECT Id,PayloadJson,CycleAttempts FROM DeliveryJobs WHERE
                (State IN('Pending','Unknown') AND NextAttemptAt <= $now) OR (State='Processing' AND LeaseUntil <= $now)
                ORDER BY CreatedAt,Id
                """ + db.Limit(1), r => (Id: r.GetString(0), Json: r.GetString(1),Cycle:r.GetInt32(2)), ("now", now.ToString("O"))).SingleOrDefault();
            if (record.Id is null) return null;
            if(record.Cycle>=OutboxProcessor.MaxAutomaticAttempts){db.Execute("UPDATE DeliveryJobs SET State='Unknown',NextAttemptAt=NULL,LeaseToken=NULL,LeaseUntil=NULL,Version=Version+1,LastError='自动尝试额度已用尽，请人工核查',UpdatedAt=$now WHERE Id=$id",("now",now.ToString("O")),("id",record.Id));return new Claim(record.Id,"",FromJson<ShipmentNotification>(record.Json),record.Cycle);}
            var lease = Guid.NewGuid().ToString("N");
            db.Execute("UPDATE DeliveryJobs SET State='Processing',Version=Version+1,Attempts=Attempts+1,CycleAttempts=CycleAttempts+1,LeaseToken=$lease,LeaseUntil=$until,UpdatedAt=$now WHERE Id=$id",
                ("lease", lease), ("until", now.AddMinutes(1).ToString("O")), ("now", now.ToString("O")), ("id", record.Id));
            return new Claim(record.Id, lease, FromJson<ShipmentNotification>(record.Json),record.Cycle+1);
        });
        if (claim is null) return false;if(claim.Lease.Length==0)return true;
        GatewayResult result;
        try
        {
            var gateway = _gateways.SingleOrDefault(g => g.CanHandle(claim.Payload.Platform));
            if (gateway is null) result = new(GatewayOutcome.Unavailable, "未配置已授权的平台发货连接器，未向平台发送。");
            else if (claim.Payload.IsPartial && !gateway.SupportsPartialShipments)
                result = new(GatewayOutcome.Unavailable, "连接器未启用按订单行数量分包发货，未向平台发送。");
            else if (claim.Payload.IsMerged && !gateway.SupportsMergedShipments)
                result = new(GatewayOutcome.Unavailable, "连接器未启用跨订单合包回传，未向平台发送。");
            else
            {
                // One bounded round trip budget stays below the lease; no DB transaction is held during I/O.
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(_callTimeout);
                result = await gateway.QueryAsync(claim.Payload, timeout.Token).WaitAsync(timeout.Token);
                if (result.Outcome == GatewayOutcome.NotFound) result = await gateway.SendAsync(claim.Payload, timeout.Token).WaitAsync(timeout.Token);
                if (result.Outcome == GatewayOutcome.NotFound) result = new(GatewayOutcome.Unknown, "发送结果无法确认，等待下一次回查。");
                if (result.Outcome == GatewayOutcome.Confirmed && string.IsNullOrWhiteSpace(result.Receipt)) result = new(GatewayOutcome.Unknown, "连接器未提供可核验回执，不能标记成功。");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            // Never expose provider responses/credentials in logs or assume failure means not applied remotely.
            result = new(GatewayOutcome.Unknown, "连接器调用中断或结果不明，后台将先回查；实物库存不会再次扣减。");
        }
        var finished = _clock.GetUtcNow();
        database.Write(db =>
        {
            var state = result.Outcome switch { GatewayOutcome.Confirmed => "Confirmed", GatewayOutcome.Rejected => "Rejected", GatewayOutcome.Unavailable => "Blocked", _ => "Unknown" };
            var message = state=="Unknown"&&claim.Cycle>=OutboxProcessor.MaxAutomaticAttempts?"自动尝试额度已用尽，结果仍不明，请人工核查。":state switch{"Confirmed"=>"平台连接器已返回可核验回执。","Blocked"=>"未配置连接器或未支持所需发货能力。","Rejected"=>"平台拒绝该请求，请核查。",_=>"结果不明，后续先回查。"};
            var receipt = result.Receipt.Length > 2000 ? result.Receipt[..2000] : result.Receipt;
            var updated = db.Execute("UPDATE DeliveryJobs SET State=$state,Version=Version+1,LastError=$error,Receipt=$receipt,NextAttemptAt=$next,LeaseToken=NULL,LeaseUntil=NULL,UpdatedAt=$now WHERE Id=$id AND State='Processing' AND LeaseToken=$lease",
                ("state", state), ("error", message), ("receipt", receipt), ("next", state == "Unknown" && claim.Cycle<OutboxProcessor.MaxAutomaticAttempts ? finished.AddSeconds(5*Math.Pow(2,claim.Cycle-1)).ToString("O") : null), ("now", finished.ToString("O")), ("id", claim.Id), ("lease", claim.Lease));
            db.Execute("INSERT INTO DeliveryAttempts(JobId,LeaseToken,Outcome,Message,Receipt,CreatedAt) VALUES($id,$lease,$outcome,$message,$receipt,$now)",
                ("id", claim.Id), ("lease", claim.Lease), ("outcome", updated == 1 ? state : "StaleLeaseIgnored"), ("message", message), ("receipt", receipt), ("now", finished.ToString("O")));
            return updated;
        });
        return true;
    }
}
