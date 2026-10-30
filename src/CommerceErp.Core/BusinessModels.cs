namespace CommerceErp.Core;

public enum ReviewState { PendingReview, Approved, Exception, OnHold, Cancelled }
public sealed record ImportOrderRequest(string OperationId, SampleOrder Order, bool AutoReview = true);
public sealed record OrderActionRequest(string OperationId, long ExpectedVersion, string Reason = "");
public sealed record CorrectOrderRequest(string OperationId, long ExpectedVersion, SampleOrder Order, string Reason);
public sealed record ReceiveStockRequest(string OperationId, decimal Quantity, string Reason);
public sealed record StockPolicyRequest(string OperationId, long ExpectedVersion, decimal Frozen, decimal Safety, string Reason);
public sealed record OrderCommandResult(string OrderId, ReviewState State, long Version, bool Replayed, string Message);
public sealed record StockCommandResult(string SkuId, long Version, bool Replayed, string Message);
public sealed record OrderSummary(string Id, string ExternalOrderId, Platform Platform, string ShopId,
    ReviewState State, long Version, decimal PaidAmount, int LineCount, DateTimeOffset CreatedAt, string IssueSummary, string ShippingState = "Unshipped", int ActiveAfterSales = 0);
public sealed record OrderPage(List<OrderSummary> Items, int Total, int Page, int PageSize);
public sealed record OrderEvent(long Id, string Action, string FromState, string ToState,
    string Reason, string RuleVersion, DateTimeOffset CreatedAt);
public sealed record ReservationView(string SkuId, string SkuCode, decimal Quantity, string State, decimal Consumed = 0);
public sealed record OrderDetails(OrderSummary Summary, SampleOrder Source, NormalizedOrder? Normalized,
    List<ValidationIssue> Issues, List<ReservationView> Reservations, List<OrderEvent> History);
public sealed record InventoryRow(string TenantId, string OwnerId, string WarehouseId, string SkuId,
    string SkuCode, string SkuName, decimal OnHand, decimal Reserved, decimal Frozen, decimal Safety, long Version, decimal Quarantined = 0)
{
    public decimal Available => Math.Max(0, OnHand - Reserved - Frozen - Safety);
}
public sealed record StockLedgerRow(long Id, string SkuCode, string Action, string? OrderId,
    decimal OnHandDelta, decimal ReservedDelta, decimal OnHandAfter, decimal ReservedAfter,
    decimal FrozenAfter, decimal SafetyAfter, string Reason, DateTimeOffset CreatedAt);
public sealed record BusinessDashboard(int TotalOrders, int PendingOrders, int ApprovedOrders,
    int ExceptionOrders, int CancelledOrders, int PendingOutbox, string Database, string RuleVersion);
public sealed record ConsistencyReport(bool Passed, int CheckedBalances, int ActiveReservations,
    int CheckedOrders, List<string> Problems);

// All quantities are exact to 0.001; SQLite INTEGER avoids REAL/decimal comparison drift.
public static class BusinessUnits
{
    public const string Owner = "demo-owner";
    public const string Warehouse = "wh-main";
    public const string RuleVersion = "review-v1";
    public static long Quantity(decimal value)
    {
        if (value < 0 || value > 1_000_000_000m || decimal.Round(value, 3) != value)
            throw new BusinessRuleException("库存数量必须在 0 至 10 亿之间且最多三位小数。");
        return checked((long)(value * 1000m));
    }
    public static long Money(decimal value) => checked((long)(value * 100m));
    public static decimal FromQuantity(long value) => value / 1000m;
    public static decimal FromMoney(long value) => value / 100m;
}
