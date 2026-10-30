using CommerceErp.Core;
using static CommerceErp.Infrastructure.CommerceDatabase;
using static CommerceErp.Core.BusinessUnits;

namespace CommerceErp.Infrastructure;

public sealed partial class OrderService
{
    private const string SummaryColumns = "Id,ExternalOrderId,Platform,ShopId,State,Version,PaidCents,LineCount,CreatedAt,IssuesJson," + ShippingStateSql + ",(SELECT COUNT(*) FROM AfterSaleCases c WHERE c.OrderId=SalesOrders.Id AND c.State IN('Open','Approved'))";
    private static OrderSummary Summary(DbRow r) => new(r.GetString(0), r.GetString(1), Enum.Parse<Platform>(r.GetString(2)), r.GetString(3),
        Enum.Parse<ReviewState>(r.GetString(4)), r.GetInt64(5), FromMoney(r.GetInt64(6)), r.GetInt32(7), DateTimeOffset.Parse(r.GetString(8)),
        string.Join("；", FromJson<List<ValidationIssue>>(r.GetString(9)).Select(i => i.Message)), r.GetString(10), r.GetInt32(11));
    public OrderPage List(int page = 1, int pageSize = 20, string? search = null, ReviewState? state = null)
    {
        if (page < 1 || page > 1_000_000 || pageSize < 1 || pageSize > 100 || search?.Length > 128 || (state.HasValue && !Enum.IsDefined(state.Value)))
            throw new BusinessRuleException("分页、搜索或状态参数不合法。");
        return _database.Read(db =>
        {
            var filter = "TenantId=$t AND ($state IS NULL OR State=$state) AND ($search='' OR CHARINDEX($search,ExternalOrderId)>0)" + SecurityContext.ShopFilter("SalesOrders");
            (string Name, object? Value)[] parameters = [("t", Tenant(db)), ("state", state?.ToString()), ("search", search?.Trim() ?? "")];
            var total = db.Scalar<int>("SELECT COUNT(*) FROM SalesOrders WHERE " + filter, parameters);
            var rows = db.Query("SELECT " + SummaryColumns + " FROM SalesOrders WHERE " + filter + " ORDER BY CreatedAt DESC,Id" + db.Page, Summary,
                [.. parameters, ("limit", pageSize), ("offset", (page - 1) * pageSize)]);
            return new OrderPage(rows, total, page, pageSize);
        });
    }
    public OrderDetails Details(string id) => _database.Read(db =>
    {
        var row = Get(db, id);
        var summary = db.Query("SELECT " + SummaryColumns + " FROM SalesOrders WHERE TenantId=$t AND Id=$id", Summary, ("t", Tenant(db)), ("id", id)).Single();
        var reservations = db.Query("SELECT r.SkuId,s.Code,CASE WHEN r.State='Active' THEN r.QuantityUnits-r.ConsumedUnits ELSE 0 END,CASE WHEN r.ConsumedUnits=r.QuantityUnits THEN 'Consumed' ELSE r.State END,r.ConsumedUnits FROM Reservations r JOIN Skus s ON s.TenantId=r.TenantId AND s.Id=r.SkuId WHERE r.TenantId=$t AND r.OrderId=$id ORDER BY s.Code",
            r => new ReservationView(r.GetString(0), r.GetString(1), FromQuantity(r.GetInt64(2)), r.GetString(3), FromQuantity(r.GetInt64(4))), ("t", Tenant(db)), ("id", id));
        var history = db.Query("SELECT Id,Action,FromState,ToState,Reason,RuleVersion,CreatedAt FROM OrderHistory WHERE OrderId=$id ORDER BY Id DESC",
            r => new OrderEvent(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), DateTimeOffset.Parse(r.GetString(6))), ("id", id));
        return new OrderDetails(summary, row.Source, row.Normalized, row.Issues, reservations, history);
    });
    public BusinessDashboard Dashboard() => _database.Read(db =>
    {
        int Count(string? state = null) => db.Scalar<int>("SELECT COUNT(*) FROM SalesOrders WHERE TenantId=$t AND ($s IS NULL OR State=$s)" + SecurityContext.ShopFilter("SalesOrders"), ("t", Tenant(db)), ("s", state));
        return new BusinessDashboard(Count(), Count("PendingReview"), Count("Approved"), Count("Exception") + Count("OnHold"), Count("Cancelled"),
            (SecurityContext.Current is not null && !ShopIds(db).All(SecurityContext.Current.User.Shops.Contains) ? 0 : db.Scalar<int>("SELECT COUNT(*) FROM Outbox WHERE TenantId=$t AND State='Pending'", ("t", Tenant(db)))), _database.ProviderLabel, RuleVersion);
    });
    public ConsistencyReport CheckConsistency() => _database.Read(db =>
    {
        var problems = new List<string>();
        var balances = db.Query("SELECT SkuId,OnHandUnits,ReservedUnits FROM InventoryBalances WHERE TenantId=$t AND OwnerId=$o AND WarehouseId=$w",
            r => (Sku: r.GetString(0), Hand: r.GetInt64(1), Reserved: r.GetInt64(2)), ("t", Tenant(db)), ("o", OwnerOf(db)), ("w", WarehouseOf(db)));
        foreach (var b in balances)
        {
            (string Name, object? Value)[] p = [("t", Tenant(db)), ("o", OwnerOf(db)), ("w", WarehouseOf(db)), ("s", b.Sku)];
            var reserved = db.Scalar<long>("SELECT COALESCE(SUM(QuantityUnits-ConsumedUnits),0) FROM Reservations WHERE TenantId=$t AND OwnerId=$o AND WarehouseId=$w AND SkuId=$s AND State='Active'", p);
            var ledger = db.Query("SELECT COALESCE(SUM(OnHandDelta),0),COALESCE(SUM(ReservedDelta),0) FROM InventoryLedger WHERE TenantId=$t AND OwnerId=$o AND WarehouseId=$w AND SkuId=$s",
                r => (Hand: r.GetInt64(0), Reserved: r.GetInt64(1)), p).Single();
            if (reserved != b.Reserved) problems.Add($"{b.Sku}：预占余额与订单预占之和不符。");
            if (ledger.Hand != b.Hand || ledger.Reserved != b.Reserved) problems.Add($"{b.Sku}：库存流水与余额不符。");
        }
        var orders = db.Query("SELECT Id FROM SalesOrders WHERE TenantId=$t", r => r.GetString(0), ("t", Tenant(db)));
        foreach (var id in orders)
        {
            var row = Get(db, id);
            var active = db.Query("SELECT SkuId,QuantityUnits FROM Reservations WHERE TenantId=$t AND OrderId=$id AND State='Active'", r => (Sku: r.GetString(0), Quantity: r.GetInt64(1)), ("t", Tenant(db)), ("id", id));
            if (row.State != ReviewState.Approved && active.Count > 0) problems.Add($"{id}：未审核订单存在有效预占。");
            if (row.State == ReviewState.Approved && (row.Normalized is null || active.Count != row.Normalized.StockRequirements.Count ||
                row.Normalized.StockRequirements.Any(q => !active.Any(a => a.Sku == q.InternalSkuId && a.Quantity == Quantity(q.Quantity)))))
                problems.Add($"{id}：已审核订单的组件需求与预占不符。");
        }
        problems.AddRange(AuditWarehouseCore(db).Problems);
        problems.AddRange(AuditAfterSalesCore(db).Problems);
        problems.AddRange(AuditFinanceCore(db).Problems);
        return new ConsistencyReport(problems.Count == 0, balances.Count,
            db.Scalar<int>("SELECT COUNT(*) FROM Reservations WHERE TenantId=$t AND State='Active' AND QuantityUnits>ConsumedUnits", ("t", Tenant(db))), orders.Count, problems);
    });
}
