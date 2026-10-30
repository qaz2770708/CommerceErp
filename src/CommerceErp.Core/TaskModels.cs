namespace CommerceErp.Core;

public enum WorkTaskKind { BusinessEvent, ShipmentDelivery }
public sealed record WorkTaskRow(WorkTaskKind Kind,string Id,string Title,string RelatedId,string RelatedNumber,string ShopId,
    string State,long Version,int Attempts,string Message,string CreatedAt,string UpdatedAt,string? NextAttemptAt,bool CanRetry);
public sealed record WorkTaskPage(List<WorkTaskRow> Items,int Total,int Page,int PageSize);
public sealed record WorkTaskSummary(int Total,int Waiting,int Processing,int Attention,int Confirmed,string? OldestWaitingAt);
public sealed record WorkTaskHistory(string Action,string Actor,string Reason,string StartedAt,string? FinishedAt);
public sealed record WorkTaskDetails(WorkTaskRow Task,List<WorkTaskHistory> History);
public sealed record RetryWorkTaskRequest(string OperationId,long ExpectedVersion,string Actor,string Reason);
public sealed record WorkTaskResult(string Id,long Version,bool Replayed,string Message);
public sealed record OutboxEvent(string Id,string TenantId,string AggregateId,long AggregateVersion,string EventType,string PayloadJson);
public enum EventOutcome { Confirmed, NotFound, Retryable, Unknown, Blocked, Rejected }
public sealed record EventDeliveryResult(EventOutcome Outcome,string Receipt="");
// Production handlers must implement an authoritative lookup and use event.Id as a stable idempotency key.
// The host always queries before sending, including after a timeout or expired lease. No production handler is registered yet.
public interface IOutboxHandler
{
    bool CanHandle(string eventType);
    Task<EventDeliveryResult> QueryAsync(OutboxEvent message,CancellationToken cancellationToken);
    Task<EventDeliveryResult> SendAsync(OutboxEvent message,CancellationToken cancellationToken);
}
