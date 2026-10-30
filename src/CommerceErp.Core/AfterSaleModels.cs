namespace CommerceErp.Core;

public enum AfterSaleKind { CancelUnshipped, RefundOnly, ReturnRefund }
public enum AfterSaleState { Open, Approved, Closed, Rejected, Withdrawn }
// EvidenceRecorded is a local operator's evidence, never a platform confirmation.
public enum RefundTrackingState { Pending, Unknown, Failed, EvidenceRecorded }
public sealed record AfterSaleSelection(string ShipmentLineId, decimal Quantity);
public sealed record CreateAfterSaleRequest(string OperationId, string OrderId, long ExpectedOrderVersion, string SourceCaseNumber,
    AfterSaleKind Kind, decimal RefundAmount, List<AfterSaleSelection> Lines, string Actor, string Reason);
public sealed record AfterSaleActionRequest(string OperationId, long ExpectedVersion, string Actor, string Reason,
    bool ConfirmPickedGoodsReturned = false, bool AcceptShortReturn = false);
public sealed record InspectionLine(string CaseLineId, string SkuId, decimal GoodQuantity, decimal DamagedQuantity);
public sealed record InspectReturnRequest(string OperationId, long ExpectedVersion, string ReceiptNumber, string ReturnTracking,
    List<InspectionLine> Lines, string Actor, string Reason);
public sealed record RecordRefundRequest(string OperationId, long ExpectedVersion, string EventKey, RefundTrackingState Status,
    decimal Amount, string RefundReference, string Evidence, DateTimeOffset ObservedAt, bool ConfirmLocalEvidence, string Actor, string Reason);
public sealed record AfterSaleResult(string Id, long Version, AfterSaleState State, bool Replayed, string Message);
public sealed record AfterSaleCandidate(string OrderId, string ExternalOrderId, Platform Platform, string ShopId, long Version,
    string ShippingState, decimal PaidAmount, decimal RemainingRefundAmount, bool CanCancelUnshipped);
public sealed record AfterSaleSourceLine(string ShipmentLineId, string ShipmentNumber, string ExternalLineId, string SkuCode,
    decimal Shipped, decimal Claimed, decimal Remaining);
public sealed record AfterSalePreparation(AfterSaleCandidate Order, List<AfterSaleSourceLine> Lines);
public sealed record AfterSaleSummary(string Id, string Number, string SourceCaseNumber, string OrderId, string ExternalOrderId,
    AfterSaleKind Kind, AfterSaleState State, RefundTrackingState RefundState, decimal RefundAmount, long Version,
    DateTimeOffset CreatedAt, string RefundReference, bool ShortReturnAccepted);
public sealed record AfterSalePage(List<AfterSaleSummary> Items, int Total, int Page, int PageSize);
public sealed record AfterSaleLineView(string Id, string? ShipmentLineId, string ShipmentNumber, string ExternalLineId, string SkuCode, decimal Quantity);
public sealed record ReturnComponentView(string CaseLineId, string ShipmentNumber, string ExternalLineId, string SkuId, string SkuCode,
    decimal Expected, decimal GoodReceived, decimal DamagedReceived)
{ public decimal Remaining => Expected - GoodReceived - DamagedReceived; }
public sealed record ReturnReceiptView(string Id, string Number, string ReturnTracking, string Actor, string Reason, DateTimeOffset CreatedAt,
    decimal GoodQuantity, decimal DamagedQuantity);
public sealed record RefundObservationView(string EventKey, RefundTrackingState Status, decimal Amount, string RefundReference, string Evidence,
    string Actor, DateTimeOffset ObservedAt, DateTimeOffset CreatedAt);
public sealed record AfterSaleHistory(long Id, string Action, string Actor, string Reason, string Data, DateTimeOffset CreatedAt);
public sealed record AfterSaleDetails(AfterSaleSummary Summary, List<AfterSaleLineView> Lines, List<ReturnComponentView> Components,
    List<ReturnReceiptView> Receipts, List<RefundObservationView> RefundObservations, List<AfterSaleHistory> History);
public sealed record QuarantineRow(string SkuId, string SkuCode, decimal Quantity, long Version);
public sealed record QuarantineMovement(long Id, string SkuCode, string CaseNumber, string ReceiptNumber, decimal Quantity,
    decimal BalanceAfter, string Actor, string Reason, DateTimeOffset CreatedAt);
public sealed record AfterSaleAudit(bool Passed, int Cases, int Receipts, List<string> Problems);
