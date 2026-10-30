namespace CommerceErp.Core;
public enum InventoryDocumentKind { Adjustment, Count, QuarantineRelease, QuarantineScrap, QuarantineReturn, Reversal }
// Value means signed delta for Adjustment, physical good count for Count, positive quantity for quarantine dispositions.
public sealed record InventoryDocumentLine(string SkuId,decimal Value,decimal QuarantineCount,long ExpectedInventoryVersion,long ExpectedQuarantineVersion,long ExpectedSkuVersion);
public sealed record InventoryDocumentRequest(string OperationId,string Number,InventoryDocumentKind Kind,List<InventoryDocumentLine> Lines,string Reason,string Evidence);
public sealed record EditInventoryDocumentRequest(string OperationId,long ExpectedVersion,List<InventoryDocumentLine> Lines,string Reason,string Evidence);
public sealed record InventoryDocumentAction(string OperationId,long ExpectedVersion,string Reason);
public sealed record ReverseInventoryDocumentRequest(string OperationId,long ExpectedVersion,string Number,string Reason,string Evidence);
public sealed record InventoryDocumentBalance(string SkuId,string Code,string Name,decimal OnHand,decimal Reserved,decimal Frozen,decimal Safety,long Version,decimal Quarantined,long QuarantineVersion,long SkuVersion);
public sealed record InventoryPreparedLine(string SkuId,decimal OnHandBefore,decimal QuarantineBefore,decimal OnHandDelta,decimal QuarantineDelta,long InventoryVersion,long QuarantineVersion,long SkuVersion);
public sealed record InventoryDocument(string Id,string Number,InventoryDocumentKind Kind,string State,long Version,string CreatedBy,string ApprovedBy,string Reason,string Evidence,string CreatedAt,string? ReversalOf,List<InventoryPreparedLine> Lines);
public sealed record InventoryDocumentResult(string Id,long Version,bool Replayed,string Message);
public sealed record InventoryMovement(string DocumentId,string SkuId,decimal OnHandDelta,decimal QuarantineDelta,decimal OnHandAfter,decimal QuarantineAfter,long InventoryVersion,long QuarantineVersion);
public sealed record InventoryDocumentHistory(string Action,string ActorId,string Reason,string CreatedAt,string DataJson);
public sealed record InventoryDocumentDetails(InventoryDocument Document,List<InventoryMovement> Movements,List<InventoryDocumentHistory> History);

public sealed record InventoryDocumentAudit(bool Passed,int CheckedDocuments,List<string> Problems);
