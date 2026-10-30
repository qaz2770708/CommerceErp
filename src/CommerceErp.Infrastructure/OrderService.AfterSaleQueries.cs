using CommerceErp.Core;
using static CommerceErp.Infrastructure.CommerceDatabase;
using static CommerceErp.Core.BusinessUnits;

namespace CommerceErp.Infrastructure;

public sealed partial class OrderService
{
    private static AfterSaleCandidate AfterCandidate(DbSession db, StoredOrder order)
    {
        var shipped = db.Scalar<int>("SELECT COUNT(*) FROM ShipmentLines l JOIN Shipments s ON s.Id=l.ShipmentId WHERE l.OrderId=$o AND s.State='Shipped'", ("o", order.Id));
        var shippingState = db.Scalar<string>("SELECT " + ShippingStateSql + " FROM SalesOrders WHERE Id=$id", ("id", order.Id));
        var claimed = ClaimedRefund(db, order.Id);
        return new(order.Id, order.Source.ExternalOrderId, order.Source.Platform, order.Source.ShopId, order.Version, shippingState,
            order.Source.PaidAmount, order.Source.PaidAmount - FromMoney(claimed), shipped == 0 && claimed == 0);
    }
    public List<AfterSaleCandidate> AfterSaleCandidates(string? search = null) => _database.Read(db =>
    {
        if (search?.Length > 128) throw new BusinessRuleException("查询过长。");
        var ids = db.Query("SELECT Id FROM SalesOrders WHERE TenantId=$t AND State='Approved' AND CHARINDEX($q,ExternalOrderId)>0" + SecurityContext.ShopFilter("SalesOrders") + " ORDER BY CreatedAt DESC,Id" + db.Limit(200), r => r.GetString(0), ("t", Tenant(db)), ("q", search?.Trim() ?? ""));
        return ids.Select(id => AfterCandidate(db, Get(db, id))).ToList();
    });
    public AfterSalePreparation PrepareAfterSale(string orderId) => _database.Read(db =>
    {
        var order = Get(db, orderId); if (order.State != ReviewState.Approved) throw new ConflictException("当前只为已审核订单创建售后。");
        var lines = db.Query("SELECT l.Id,s.Number,l.ExternalLineId,l.SnapshotJson,l.QuantityUnits FROM ShipmentLines l JOIN Shipments s ON s.Id=l.ShipmentId WHERE l.OrderId=$id AND s.State='Shipped' ORDER BY s.Number,l.ExternalLineId",
            r => (Id: r.GetString(0), Number: r.GetString(1), External: r.GetString(2), Snapshot: FromJson<NormalizedLine>(r.GetString(3)), Units: r.GetInt64(4)), ("id", orderId));
        return new AfterSalePreparation(AfterCandidate(db, order), lines.Select(l => new AfterSaleSourceLine(l.Id, l.Number, l.External, l.Snapshot.InternalSkuCode, FromQuantity(l.Units),
            FromQuantity(ClaimedShipmentUnits(db, l.Id)), FromQuantity(l.Units - ClaimedShipmentUnits(db, l.Id)))).ToList());
    });
    private const string AfterColumns = "c.Id,c.Number,c.SourceCaseNumber,c.OrderId,o.ExternalOrderId,c.Kind,c.State,c.RefundState,c.RefundCents,c.Version,c.CreatedAt,c.RefundReference,c.ShortReturnAccepted";
    private static AfterSaleSummary AfterSummary(DbRow r) => new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4),
        Enum.Parse<AfterSaleKind>(r.GetString(5)), Enum.Parse<AfterSaleState>(r.GetString(6)), Enum.Parse<RefundTrackingState>(r.GetString(7)), FromMoney(r.GetInt64(8)), r.GetInt64(9), DateTimeOffset.Parse(r.GetString(10)), r.GetString(11), r.GetInt64(12) == 1);
    public AfterSalePage AfterSales(int page = 1, int pageSize = 20, string? search = null, AfterSaleState? state = null) => _database.Read(db =>
    {
        if (page < 1 || page > 1_000_000 || pageSize < 1 || pageSize > 100 || search?.Length > 128 || (state.HasValue && !Enum.IsDefined(state.Value))) throw new BusinessRuleException("售后查询参数不合法。");
        var filter = "c.TenantId=$t AND ($state IS NULL OR c.State=$state) AND (CHARINDEX($q,c.Number)>0 OR CHARINDEX($q,c.SourceCaseNumber)>0 OR CHARINDEX($q,o.ExternalOrderId)>0)" + SecurityContext.ShopFilter("c");
        (string Name, object? Value)[] p = [("t", Tenant(db)), ("state", state?.ToString()), ("q", search?.Trim() ?? "")];
        var total = db.Scalar<int>("SELECT COUNT(*) FROM AfterSaleCases c JOIN SalesOrders o ON o.Id=c.OrderId WHERE " + filter, p);
        var rows = db.Query("SELECT " + AfterColumns + " FROM AfterSaleCases c JOIN SalesOrders o ON o.Id=c.OrderId WHERE " + filter + " ORDER BY c.CreatedAt DESC,c.Id" + db.Page, AfterSummary, [.. p, ("limit", pageSize), ("offset", (page - 1) * pageSize)]);
        return new AfterSalePage(rows, total, page, pageSize);
    });
    public AfterSaleDetails AfterSaleDetails(string id) => _database.Read(db =>
    {
        _ = AfterCase(db, id);
        var summary = db.Query("SELECT " + AfterColumns + " FROM AfterSaleCases c JOIN SalesOrders o ON o.Id=c.OrderId WHERE c.Id=$id", AfterSummary, ("id", id)).Single();
        var lines = db.Query("SELECT l.Id,l.ShipmentLineId,COALESCE(s.Number,''),l.ExternalLineId,l.SnapshotJson,l.QuantityUnits FROM AfterSaleLines l LEFT JOIN ShipmentLines sl ON sl.Id=l.ShipmentLineId LEFT JOIN Shipments s ON s.Id=sl.ShipmentId WHERE l.CaseId=$id ORDER BY s.Number,l.ExternalLineId",
            r => new AfterSaleLineView(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetString(2), r.GetString(3), FromJson<NormalizedLine>(r.GetString(4)).InternalSkuCode, FromQuantity(r.GetInt64(5))), ("id", id));
        var components = db.Query("""
            SELECT c.CaseLineId,s.Number,l.ExternalLineId,c.SkuId,sku.Code,c.ExpectedUnits,c.GoodUnits,c.DamagedUnits
            FROM ReturnComponents c JOIN AfterSaleLines l ON l.Id=c.CaseLineId JOIN ShipmentLines sl ON sl.Id=l.ShipmentLineId JOIN Shipments s ON s.Id=sl.ShipmentId
            JOIN Skus sku ON sku.Id=c.SkuId AND sku.TenantId=$t WHERE l.CaseId=$id ORDER BY s.Number,l.ExternalLineId,sku.Code
            """, r => new ReturnComponentView(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), FromQuantity(r.GetInt64(5)), FromQuantity(r.GetInt64(6)), FromQuantity(r.GetInt64(7))), ("t", Tenant(db)), ("id", id));
        var receipts = db.Query("""
            SELECT r.Id,r.ReceiptNumber,r.ReturnTracking,r.Actor,r.Reason,r.CreatedAt,COALESCE(SUM(l.GoodUnits),0),COALESCE(SUM(l.DamagedUnits),0)
            FROM ReturnReceipts r LEFT JOIN ReturnReceiptLines l ON l.ReceiptId=r.Id WHERE r.CaseId=$id GROUP BY r.Id,r.ReceiptNumber,r.ReturnTracking,r.Actor,r.Reason,r.CreatedAt ORDER BY r.CreatedAt DESC
            """, r => new ReturnReceiptView(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), DateTimeOffset.Parse(r.GetString(5)), FromQuantity(r.GetInt64(6)), FromQuantity(r.GetInt64(7))), ("id", id));
        var refunds = db.Query("SELECT EventKey,Status,AmountCents,RefundReference,Evidence,Actor,ObservedAt,CreatedAt FROM RefundObservations WHERE CaseId=$id ORDER BY Id DESC",
            r => new RefundObservationView(r.GetString(0), Enum.Parse<RefundTrackingState>(r.GetString(1)), FromMoney(r.GetInt64(2)), r.GetString(3), r.GetString(4), r.GetString(5), DateTimeOffset.Parse(r.GetString(6)), DateTimeOffset.Parse(r.GetString(7))), ("id", id));
        var history = db.Query("SELECT Id,Action,Actor,Reason,DataJson,CreatedAt FROM AfterSaleHistory WHERE CaseId=$id ORDER BY Id DESC",
            r => new AfterSaleHistory(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), DateTimeOffset.Parse(r.GetString(5))), ("id", id));
        return new AfterSaleDetails(summary, lines, components, receipts, refunds, history);
    });
    public List<QuarantineRow> Quarantine() => _database.Read(db => db.Query("SELECT b.SkuId,s.Code,b.QuantityUnits,b.Version FROM QuarantineBalances b JOIN Skus s ON s.Id=b.SkuId AND s.TenantId=b.TenantId WHERE b.TenantId=$t ORDER BY s.Code",
        r => new QuarantineRow(r.GetString(0), r.GetString(1), FromQuantity(r.GetInt64(2)), r.GetInt64(3)), ("t", Tenant(db))));
    public List<QuarantineMovement> QuarantineLedger() => _database.Read(db => db.Query("""
        SELECT q.Id,s.Code,c.Number,r.ReceiptNumber,q.QuantityUnits,q.BalanceAfter,q.Actor,q.Reason,q.CreatedAt FROM QuarantineLedger q
        JOIN Skus s ON s.Id=q.SkuId AND s.TenantId=q.TenantId JOIN AfterSaleCases c ON c.Id=q.CaseId JOIN ReturnReceipts r ON r.Id=q.ReceiptId
        WHERE q.TenantId=$t
        """ + SecurityContext.ShopFilter("c") + " ORDER BY q.Id DESC" + db.Limit(200), r => new QuarantineMovement(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), FromQuantity(r.GetInt64(4)), FromQuantity(r.GetInt64(5)), r.GetString(6), r.GetString(7), DateTimeOffset.Parse(r.GetString(8))), ("t", Tenant(db))));
    public AfterSaleAudit AuditAfterSales() => _database.Read(AuditAfterSalesCore);
    private static AfterSaleAudit AuditAfterSalesCore(DbSession db)
    {
        var problems = new List<string>();
        void Check(string sql, string message) { if (db.Scalar<int>(sql) > 0) problems.Add(message); }
        Check("""
            SELECT COUNT(*) FROM ShipmentLines sl WHERE (SELECT COALESCE(SUM(l.QuantityUnits),0) FROM AfterSaleLines l JOIN AfterSaleCases c ON c.Id=l.CaseId WHERE l.ShipmentLineId=sl.Id AND c.State NOT IN('Rejected','Withdrawn'))>sl.QuantityUnits
            """, "售后申领量超过原出库行数量。");
        Check("SELECT COUNT(*) FROM SalesOrders o WHERE (SELECT COALESCE(SUM(RefundCents),0) FROM AfterSaleCases c WHERE c.OrderId=o.Id AND c.State NOT IN('Rejected','Withdrawn'))>o.PaidCents", "售后金额超过原单实付。");
        Check("""
            SELECT COUNT(*) FROM ReturnComponents c WHERE c.GoodUnits<>COALESCE((SELECT SUM(l.GoodUnits) FROM ReturnReceiptLines l WHERE l.CaseLineId=c.CaseLineId AND l.SkuId=c.SkuId),0)
            OR c.DamagedUnits<>COALESCE((SELECT SUM(l.DamagedUnits) FROM ReturnReceiptLines l WHERE l.CaseLineId=c.CaseLineId AND l.SkuId=c.SkuId),0)
            """, "退货组件累计量与收货明细不符。");
        Check("""
            SELECT COUNT(*) FROM ReturnReceiptLines rl JOIN ReturnReceipts r ON r.Id=rl.ReceiptId JOIN AfterSaleLines l ON l.Id=rl.CaseLineId WHERE r.CaseId<>l.CaseId
            """, "收货明细归属售后单不符。");
        Check("""
            WITH keys AS (SELECT ReceiptId,SkuId FROM ReturnReceiptLines UNION SELECT ReturnReceiptId,SkuId FROM InventoryLedger WHERE ReturnReceiptId IS NOT NULL UNION SELECT ReceiptId,SkuId FROM QuarantineLedger)
            SELECT COUNT(*) FROM keys k WHERE
            COALESCE((SELECT SUM(GoodUnits) FROM ReturnReceiptLines WHERE ReceiptId=k.ReceiptId AND SkuId=k.SkuId),0)<>COALESCE((SELECT SUM(OnHandDelta) FROM InventoryLedger WHERE ReturnReceiptId=k.ReceiptId AND SkuId=k.SkuId AND Action='ReturnGood'),0)
            OR COALESCE((SELECT SUM(DamagedUnits) FROM ReturnReceiptLines WHERE ReceiptId=k.ReceiptId AND SkuId=k.SkuId),0)<>COALESCE((SELECT SUM(QuantityUnits) FROM QuarantineLedger WHERE ReceiptId=k.ReceiptId AND SkuId=k.SkuId),0)
            OR COALESCE((SELECT SUM(ReservedDelta) FROM InventoryLedger WHERE ReturnReceiptId=k.ReceiptId AND SkuId=k.SkuId),0)<>0
            """, "逐 SKU 退货良品/残次品与库存流水不符。");
        Check("""
            SELECT COUNT(*) FROM QuarantineBalances b WHERE b.QuantityUnits<>COALESCE((SELECT SUM(q.QuantityUnits) FROM QuarantineLedger q WHERE q.TenantId=b.TenantId AND q.OwnerId=b.OwnerId AND q.WarehouseId=b.WarehouseId AND q.SkuId=b.SkuId),0)+COALESCE((SELECT SUM(m.QuarantineDelta) FROM InventoryMovements m WHERE m.SkuId=b.SkuId),0)
            """, "隔离库存余额与流水不符。");
        Check("""
            SELECT COUNT(*) FROM AfterSaleCases c WHERE
            (c.RefundState='EvidenceRecorded' AND NOT EXISTS(SELECT 1 FROM RefundEvidence e JOIN RefundObservations o ON o.Id=e.ObservationId WHERE e.CaseId=c.Id AND e.AmountCents=c.RefundCents AND e.RefundReference=c.RefundReference AND o.CaseId=c.Id AND o.Status='EvidenceRecorded'))
            OR (c.RefundState<>'EvidenceRecorded' AND EXISTS(SELECT 1 FROM RefundEvidence e WHERE e.CaseId=c.Id))
            OR (c.State='Closed' AND c.RefundState<>'EvidenceRecorded')
            OR (c.State IN('Rejected','Withdrawn') AND (EXISTS(SELECT 1 FROM ReturnReceipts r WHERE r.CaseId=c.Id) OR EXISTS(SELECT 1 FROM RefundEvidence e WHERE e.CaseId=c.Id)))
            """, "售后状态、退款凭证或收货记录不一致。");
        Check("""
            SELECT COUNT(*) FROM AfterSaleCases c WHERE c.Kind='CancelUnshipped' AND c.State IN('Approved','Closed') AND
            ((SELECT State FROM SalesOrders WHERE Id=c.OrderId)<>'Cancelled' OR EXISTS(SELECT 1 FROM Reservations r WHERE r.OrderId=c.OrderId AND r.State='Active')
            OR EXISTS(SELECT 1 FROM ShipmentLines l JOIN Shipments s ON s.Id=l.ShipmentId WHERE l.OrderId=c.OrderId AND s.State NOT IN('Voided','Superseded')))
            """, "未出库售后取消后仍有有效订单、预占或出库单。");
        Check("""
            SELECT COUNT(*) FROM AfterSaleCases c WHERE c.Kind='ReturnRefund' AND c.State='Closed' AND
            (NOT EXISTS(SELECT 1 FROM ReturnReceipts r WHERE r.CaseId=c.Id) OR (c.ShortReturnAccepted=0 AND EXISTS(SELECT 1 FROM ReturnComponents rc JOIN AfterSaleLines l ON l.Id=rc.CaseLineId WHERE l.CaseId=c.Id AND rc.GoodUnits+rc.DamagedUnits<rc.ExpectedUnits)))
            """, "退货结案缺少收货或差异确认。");
        var components = db.Query("""
            SELECT c.ExpectedUnits,sc.QuantityUnits,l.QuantityUnits,sl.QuantityUnits FROM ReturnComponents c JOIN AfterSaleLines l ON l.Id=c.CaseLineId
            JOIN ShipmentLines sl ON sl.Id=l.ShipmentLineId LEFT JOIN ShipmentComponents sc ON sc.ShipmentLineId=sl.Id AND sc.SkuId=c.SkuId
            """, r => (Expected: r.GetInt64(0), Source: r.IsDBNull(1) ? 0 : r.GetInt64(1), Claimed: r.GetInt64(2), Shipped: r.GetInt64(3)));
        if (components.Any(c => (decimal)c.Expected * c.Shipped != (decimal)c.Source * c.Claimed)) problems.Add("退货组件需求与原出库快照不符。");
        Check("""
            SELECT COUNT(*) FROM AfterSaleLines l JOIN AfterSaleCases c ON c.Id=l.CaseId JOIN ShipmentComponents sc ON sc.ShipmentLineId=l.ShipmentLineId
            WHERE c.Kind='ReturnRefund' AND NOT EXISTS(SELECT 1 FROM ReturnComponents rc WHERE rc.CaseLineId=l.Id AND rc.SkuId=sc.SkuId)
            """, "退货售后缺少原出库组件。");
        return new(problems.Count == 0, db.Scalar<int>("SELECT COUNT(*) FROM AfterSaleCases"), db.Scalar<int>("SELECT COUNT(*) FROM ReturnReceipts"), problems);
    }
}
