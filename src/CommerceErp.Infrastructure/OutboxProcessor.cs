using CommerceErp.Core;
using static CommerceErp.Infrastructure.CommerceDatabase;

namespace CommerceErp.Infrastructure;

public sealed class OutboxProcessor(CommerceDatabase database,IEnumerable<IOutboxHandler> handlers,TimeProvider? timeProvider=null,Action<string>? fault=null,TimeSpan? callTimeout=null)
{
    private readonly TimeSpan _callTimeout=callTimeout is{} value&&value>TimeSpan.Zero&&value<=TimeSpan.FromSeconds(35)?value:callTimeout is null?TimeSpan.FromSeconds(35):throw new ArgumentOutOfRangeException(nameof(callTimeout));
    private readonly TimeProvider _clock=timeProvider??TimeProvider.System;
    private readonly IOutboxHandler[] _handlers=handlers.ToArray();
    public const int MaxAutomaticAttempts=5;
    private sealed record Claim(OutboxEvent Event,string Lease,int Cycle);
    public async Task<bool> ProcessNextAsync(CancellationToken cancellationToken=default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now=_clock.GetUtcNow();
        var claim=database.Write(db=>
        {
            var row=db.Query("""
                SELECT Id,TenantId,AggregateId,AggregateVersion,EventType,PayloadJson,Attempts,CycleAttempts,LeaseToken
                FROM Outbox WHERE ((State IN('Pending','RetryWait','Unknown') AND NextAttemptAt<=$now) OR (State='Processing' AND LeaseUntil<=$now))
                AND NOT EXISTS(SELECT 1 FROM Outbox previous WHERE previous.TenantId=Outbox.TenantId AND previous.AggregateId=Outbox.AggregateId AND previous.State<>'Confirmed'
                    AND (previous.AggregateVersion<Outbox.AggregateVersion OR (previous.AggregateVersion=Outbox.AggregateVersion AND (previous.CreatedAt<Outbox.CreatedAt OR (previous.CreatedAt=Outbox.CreatedAt AND previous.Id<Outbox.Id)))))
                ORDER BY CreatedAt,Id
                """+db.Limit(1),r=>new{Message=new OutboxEvent(r.GetString(0),r.GetString(1),r.GetString(2),r.GetInt64(3),r.GetString(4),r.GetString(5)),Attempts=r.GetInt32(6),Cycle=r.GetInt32(7),Lease=r.IsDBNull(8)?null:r.GetString(8)},("now",now.ToString("O"))).SingleOrDefault();
            if(row is null)return null;
            if(row.Lease is not null)db.Execute("UPDATE OutboxAttempts SET Outcome='LeaseExpired',FinishedAt=$now WHERE LeaseToken=$lease AND FinishedAt IS NULL",("now",now.ToString("O")),("lease",row.Lease));
            // An interrupted call may have reached its destination. At the retry limit leave it unresolved for an operator.
            if(row.Cycle>=MaxAutomaticAttempts)
            {db.Execute("UPDATE Outbox SET State='Unknown',Version=Version+1,LastCode='RetryLimit',NextAttemptAt=NULL,LeaseToken=NULL,LeaseUntil=NULL,UpdatedAt=$now WHERE Id=$id",("now",now.ToString("O")),("id",row.Message.Id));return new Claim(row.Message,"",row.Cycle);}
            var lease=Guid.NewGuid().ToString("N");
            db.Execute("UPDATE Outbox SET State='Processing',Version=Version+1,Attempts=Attempts+1,CycleAttempts=CycleAttempts+1,LeaseToken=$lease,LeaseUntil=$until,NextAttemptAt=NULL,UpdatedAt=$now WHERE Id=$id",
                ("lease",lease),("until",now.AddMinutes(1).ToString("O")),("now",now.ToString("O")),("id",row.Message.Id));
            db.Execute("INSERT INTO OutboxAttempts VALUES($lease,$id,$attempt,'Processing',$now,NULL)",("lease",lease),("id",row.Message.Id),("attempt",row.Attempts+1),("now",now.ToString("O")));
            return new Claim(row.Message,lease,row.Cycle+1);
        });
        if(claim is null)return false;if(claim.Lease.Length==0)return true;
        fault?.Invoke("after-claim");
        EventDeliveryResult result;var code="";
        try
        {
            var matching=_handlers.Where(h=>h.CanHandle(claim.Event.EventType)).ToArray();
            if(matching.Length!=1){result=new(EventOutcome.Blocked);code=matching.Length==0?"NotConfigured":"HandlerConflict";}
            else
            {
                using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);deadline.CancelAfter(_callTimeout);
                result=await matching[0].QueryAsync(claim.Event,deadline.Token).WaitAsync(deadline.Token);
                if(result.Outcome==EventOutcome.NotFound)result=await matching[0].SendAsync(claim.Event,deadline.Token).WaitAsync(deadline.Token);
                if(result.Outcome==EventOutcome.NotFound||!Enum.IsDefined(result.Outcome))result=new(EventOutcome.Unknown);
                if(result.Outcome==EventOutcome.Confirmed&&(string.IsNullOrWhiteSpace(result.Receipt)||result.Receipt.Length>2000))result=new(EventOutcome.Unknown);
                code=result.Outcome.ToString();
            }
        }
        catch(OperationCanceledException) when(cancellationToken.IsCancellationRequested){throw;}
        catch(Exception){result=new(EventOutcome.Unknown);code="CallInterrupted";}
        fault?.Invoke("after-send");
        var finished=_clock.GetUtcNow();
        database.Write(db=>
        {
            var state=result.Outcome switch{EventOutcome.Confirmed=>"Confirmed",EventOutcome.Blocked=>"Blocked",EventOutcome.Rejected=>"Failed",EventOutcome.Retryable=>"RetryWait",_=>"Unknown"};
            var retry=state is "RetryWait" or "Unknown";
            if(retry&&claim.Cycle>=MaxAutomaticAttempts){if(state=="RetryWait")state="Failed";code="RetryLimit";}
            string? next=retry&&claim.Cycle<MaxAutomaticAttempts?finished.AddSeconds(Math.Min(1800,5*Math.Pow(2,claim.Cycle-1))).ToString("O"):null;
            var count=db.Execute("UPDATE Outbox SET State=$state,Version=Version+1,LastCode=$code,Receipt=$receipt,NextAttemptAt=$next,LeaseToken=NULL,LeaseUntil=NULL,UpdatedAt=$now WHERE Id=$id AND State='Processing' AND LeaseToken=$lease",
                ("state",state),("code",code),("receipt",state=="Confirmed"?result.Receipt:""),("next",next),("now",finished.ToString("O")),("id",claim.Event.Id),("lease",claim.Lease));
            db.Execute("UPDATE OutboxAttempts SET Outcome=$outcome,FinishedAt=$now WHERE LeaseToken=$lease AND FinishedAt IS NULL",("outcome",count==1?state:"StaleLeaseIgnored"),("now",finished.ToString("O")),("lease",claim.Lease));
            fault?.Invoke("before-finish-commit");return count;
        });
        return true;
    }
}
