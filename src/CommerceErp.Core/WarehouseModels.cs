namespace CommerceErp.Core;

public enum ShipmentState { Draft, Picking, Packed, Shipped, Voided, Superseded }
public sealed record ShippingAddress(string Recipient, string Phone, string Province, string City, string District, string Street, string PostalCode = "");
public sealed record ShippingProfile(string OrderId, ShippingAddress Address, bool AllowMerge, long OrderVersion);
public sealed record SaveShippingProfileRequest(string OperationId, long ExpectedVersion, ShippingAddress Address, bool AllowMerge, string Actor, string Reason);
public sealed record AllocationLine(string OrderId, string ExternalLineId, decimal Quantity, long ExpectedOrderVersion);
public sealed record CreateShipmentRequest(string OperationId, List<AllocationLine> Lines, string Actor, string Reason);
public sealed record WarehouseActionRequest(string OperationId, long ExpectedVersion, string Actor, string Reason = "");
public sealed record ScanShipmentRequest(string OperationId, long ExpectedVersion, string Barcode, decimal Quantity, string Actor);
public sealed record WaybillRequest(string OperationId, long ExpectedVersion, string Carrier, string TrackingNumber, string Actor, string Reason);
public sealed record SplitQuantity(string LineId, decimal Quantity);
public sealed record SplitShipmentRequest(string OperationId, long ExpectedVersion, List<SplitQuantity> Lines, string Actor, string Reason);
public sealed record ShipmentVersion(string Id, long Version);
public sealed record MergeShipmentsRequest(string OperationId, List<ShipmentVersion> Shipments, string Actor, string Reason);
public sealed record WarehouseResult(string Id, long Version, string State, bool Replayed, string Message, List<string> RelatedIds);
public sealed record ShippingCandidate(string OrderId, string ExternalOrderId, Platform Platform, string ShopId, long Version, string ShippingState, bool HasAddress);
public sealed record AllocatableLine(string ExternalLineId, string SkuCode, decimal Ordered, decimal Allocated, decimal Shipped, decimal Remaining);
public sealed record OrderShippingPreparation(ShippingCandidate Order, ShippingProfile? Profile, List<AllocatableLine> Lines);
public sealed record ShipmentSummary(string Id, string Number, ShipmentState State, long Version, int OrderCount, decimal SaleQuantity, string Recipient,
    string Carrier, string TrackingNumber, DateTimeOffset CreatedAt, bool AfterSaleHold = false);
public sealed record ShipmentLineView(string Id, string OrderId, string ExternalOrderId, string ExternalLineId, string SkuCode, decimal Quantity);
public sealed record ShipmentCheckView(string SkuId, string SkuCode, string Name, decimal Required, decimal Verified);
public sealed record WarehouseHistory(long Id, string Action, string Actor, string Reason, string Data, DateTimeOffset CreatedAt);
public sealed record ShipmentDetails(ShipmentSummary Summary, ShippingAddress Address, List<ShipmentLineView> Lines,
    List<ShipmentCheckView> Checks, List<WarehouseHistory> History, List<DeliveryJobView> Deliveries);
public sealed record ShipmentPage(List<ShipmentSummary> Items, int Total, int Page, int PageSize);
public sealed record BarcodeRow(string Barcode, string SkuId, string SkuCode, long Version);
public sealed record SaveBarcodeRequest(string OperationId, long ExpectedVersion, string Barcode, string SkuId, string Actor, string Reason);
public sealed record DeliveryJobView(string Id, string ShipmentId, string ShipmentNumber, string OrderId, string ExternalOrderId, string State,
    long Version, int Attempts, string LastError, string Receipt, DateTimeOffset? NextAttemptAt);
public sealed record ShipmentNotificationLine(string ExternalLineId, string PlatformProductId, string PlatformSkuId, decimal Quantity);
public sealed record ShipmentNotification(string DeliveryId, string ShipmentId, string ShipmentNumber, Platform Platform, string ShopId, string ExternalOrderId,
    string Carrier, string TrackingNumber, bool IsPartial, bool IsMerged, List<ShipmentNotificationLine> Lines);
public enum GatewayOutcome { Confirmed, NotFound, Rejected, Unknown, Unavailable }
public sealed record GatewayResult(GatewayOutcome Outcome, string Message, string Receipt = "");
public interface IShipmentGateway
{
    bool CanHandle(Platform platform);
    bool SupportsPartialShipments => false;
    bool SupportsMergedShipments => false;
    Task<GatewayResult> QueryAsync(ShipmentNotification notification, CancellationToken cancellationToken);
    Task<GatewayResult> SendAsync(ShipmentNotification notification, CancellationToken cancellationToken);
}
public sealed record WarehouseAudit(bool Passed, int Shipments, int Dispatched, List<string> Problems);
