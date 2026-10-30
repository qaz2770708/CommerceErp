using CommerceErp.Core;
using static CommerceErp.Infrastructure.CommerceDatabase;
using static CommerceErp.Core.BusinessUnits;

namespace CommerceErp.Infrastructure;

public sealed partial class OrderService
{
    private static void Ledger(DbSession db, string sku, string operationId, string action, string? orderId, long onHandDelta, long reservedDelta, string reason)
    {
        var count = db.Execute("""
            INSERT INTO InventoryLedger(TenantId,OwnerId,WarehouseId,SkuId,OrderId,OperationId,Action,OnHandDelta,ReservedDelta,OnHandAfter,ReservedAfter,FrozenAfter,SafetyAfter,Reason,CreatedAt)
            SELECT TenantId,OwnerId,WarehouseId,SkuId,$order,$op,$action,$handDelta,$reservedDelta,OnHandUnits,ReservedUnits,FrozenUnits,SafetyUnits,$reason,$now
            FROM InventoryBalances WHERE TenantId=$t AND OwnerId=$o AND WarehouseId=$w AND SkuId=$s
            """, ("order", orderId), ("op", operationId), ("action", action), ("handDelta", onHandDelta), ("reservedDelta", reservedDelta),
            ("reason", reason), ("now", Now()), ("t", Tenant(db)), ("o", OwnerOf(db)), ("w", WarehouseOf(db)), ("s", sku));
        if (count != 1) throw new InvalidDataException("库存流水写入数量异常。");
    }
    private StockCommandResult StockCommand(string operationId, string kind, object request, Func<DbSession, StockCommandResult> action)
    {
        OperationId(operationId); var hash = Hash(request);
        return _database.Write(db =>
        {
            var replay = Replay<StockCommandResult>(db, operationId, kind, hash);
            if (replay is not null) return replay with { Replayed = true, Message = "该库存操作已处理，没有重复变动。" };
            var result = action(db); SaveOperation(db, operationId, kind, hash, result); _fault?.Invoke("before-commit"); return result;
        });
    }
    public StockCommandResult Receive(string sku, ReceiveStockRequest request) => StockCommand(request.OperationId, "Receive", new { sku, request }, db =>
    {
        if(BusinessMode(db)&&SecurityContext.Current is not null)throw new BusinessRuleException("正式工作区的人工补货请使用库存调整单并审核，禁止快捷入账。");
        Reason(request.Reason); if(!ReadCatalog(db).Skus.Any(s=>s.Id==sku&&s.Enabled&&s.Components.Count==0))throw new BusinessRuleException("收货须选择已启用的普通商品。"); var units = Quantity(request.Quantity);
        if (units <= 0 || request.Quantity > 1_000_000) throw new BusinessRuleException("补货数量须大于 0 且不超过 100 万。");
        var count = db.Execute("UPDATE InventoryBalances SET OnHandUnits=OnHandUnits+$q,Version=Version+1 WHERE TenantId=$t AND OwnerId=$o AND WarehouseId=$w AND SkuId=$s AND OnHandUnits <= 1000000000000-$q",
            ("q", units), ("t", Tenant(db)), ("o", OwnerOf(db)), ("w", WarehouseOf(db)), ("s", sku));
        if (count != 1) throw new BusinessRuleException("库存记录不存在或超过数量上限。");
        Ledger(db, sku, request.OperationId, "Receive", null, units, 0, request.Reason);
        var version = StockVersion(db, sku); Event(db, "stock:" + sku, version, "StockReceived", new { sku, request.Quantity, version });
        return new(sku, version, false, "补货已入账并记录库存流水。");
    });
    public StockCommandResult Policy(string sku, StockPolicyRequest request) => StockCommand(request.OperationId, "Policy", new { sku, request }, db =>
    {
        Reason(request.Reason); var frozen = Quantity(request.Frozen); var safety = Quantity(request.Safety);
        if (StockVersion(db, sku) != request.ExpectedVersion) throw new ConflictException("库存版本已变化，请刷新后修改策略。");
        var count = db.Execute("UPDATE InventoryBalances SET FrozenUnits=$f,SafetyUnits=$safe,Version=Version+1 WHERE TenantId=$t AND OwnerId=$o AND WarehouseId=$w AND SkuId=$s AND OnHandUnits-ReservedUnits >= $f",
            ("f", frozen), ("safe", safety), ("t", Tenant(db)), ("o", OwnerOf(db)), ("w", WarehouseOf(db)), ("s", sku));
        if (count != 1) throw new BusinessRuleException("冻结量不能占用已经预占的库存，也不能超过剩余在库量。");
        Ledger(db, sku, request.OperationId, "Policy", null, 0, 0, request.Reason);
        var version = StockVersion(db, sku); Event(db, "stock:" + sku, version, "StockPolicyChanged", new { sku, request.Frozen, request.Safety, version });
        return new(sku, version, false, "冻结量和安全库存已更新，已有预占未改变。");
    });
    private static long StockVersion(DbSession db, string sku)
    {
        var version = db.Scalar<long>("SELECT Version FROM InventoryBalances WHERE TenantId=$t AND OwnerId=$o AND WarehouseId=$w AND SkuId=$s",
            ("t", Tenant(db)), ("o", OwnerOf(db)), ("w", WarehouseOf(db)), ("s", sku));
        return version == 0 ? throw new BusinessRuleException("库存记录不存在。") : version;
    }
    public List<InventoryRow> Inventory() => _database.Read(db => db.Query("""
        SELECT b.TenantId,b.OwnerId,b.WarehouseId,b.SkuId,s.Code,s.Name,b.OnHandUnits,b.ReservedUnits,b.FrozenUnits,b.SafetyUnits,b.Version,
        COALESCE((SELECT q.QuantityUnits FROM QuarantineBalances q WHERE q.TenantId=b.TenantId AND q.OwnerId=b.OwnerId AND q.WarehouseId=b.WarehouseId AND q.SkuId=b.SkuId),0)
        FROM InventoryBalances b JOIN Skus s ON s.TenantId=b.TenantId AND s.Id=b.SkuId WHERE b.TenantId=$t ORDER BY s.Code
        """, r => new InventoryRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5),
            FromQuantity(r.GetInt64(6)), FromQuantity(r.GetInt64(7)), FromQuantity(r.GetInt64(8)), FromQuantity(r.GetInt64(9)), r.GetInt64(10), FromQuantity(r.GetInt64(11))), ("t", Tenant(db))));
    public List<StockLedgerRow> StockLedger(string? sku = null) => _database.Read(db => db.Query("""
        SELECT l.Id,s.Code,l.Action,l.OrderId,l.OnHandDelta,l.ReservedDelta,l.OnHandAfter,l.ReservedAfter,l.FrozenAfter,l.SafetyAfter,l.Reason,l.CreatedAt
        FROM InventoryLedger l JOIN Skus s ON s.TenantId=l.TenantId AND s.Id=l.SkuId WHERE l.TenantId=$t AND ($sku IS NULL OR l.SkuId=$sku)
        """ + (SecurityContext.Current is null?"":" AND (l.OrderId IS NULL OR EXISTS(SELECT 1 FROM SalesOrders access_order WHERE access_order.Id=l.OrderId"+SecurityContext.ShopFilter("access_order")+")) AND (l.ShipmentId IS NULL OR EXISTS(SELECT 1 FROM Shipments access_shipment WHERE access_shipment.Id=l.ShipmentId"+SecurityContext.ShopFilter("access_shipment")+"))") + " ORDER BY l.Id DESC" + db.Limit(200), r => new StockLedgerRow(r.GetInt64(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
            FromQuantity(r.GetInt64(4)), FromQuantity(r.GetInt64(5)), FromQuantity(r.GetInt64(6)), FromQuantity(r.GetInt64(7)),
            FromQuantity(r.GetInt64(8)), FromQuantity(r.GetInt64(9)), r.GetString(10), DateTimeOffset.Parse(r.GetString(11))), ("t", Tenant(db)), ("sku", sku)));
}
