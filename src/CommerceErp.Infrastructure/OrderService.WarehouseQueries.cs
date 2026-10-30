using CommerceErp.Core;
using static CommerceErp.Infrastructure.CommerceDatabase;
using static CommerceErp.Core.BusinessUnits;

namespace CommerceErp.Infrastructure;

public sealed partial class OrderService
{
    private const string ShippingStateSql = """
        CASE WHEN NOT EXISTS(SELECT 1 FROM ShipmentLines fl JOIN Shipments fs ON fs.Id=fl.ShipmentId WHERE fl.OrderId=SalesOrders.Id AND fs.State='Shipped') THEN 'Unshipped'
        WHEN (SELECT COALESCE(SUM(fl.QuantityUnits),0) FROM ShipmentLines fl JOIN Shipments fs ON fs.Id=fl.ShipmentId WHERE fl.OrderId=SalesOrders.Id AND fs.State='Shipped')
        = (SELECT COALESCE(SUM(ol.QuantityUnits),0) FROM OrderLines ol WHERE ol.OrderId=SalesOrders.Id) THEN 'Complete' ELSE 'Partial' END
        """;
    private static ShippingCandidate Candidate(DbRow r) => new(r.GetString(0), r.GetString(1), Enum.Parse<Platform>(r.GetString(2)), r.GetString(3), r.GetInt64(4), r.GetString(5), r.GetInt64(6) == 1);
    private static ShippingCandidate CandidateById(DbSession db, string id) => db.Query("SELECT Id,ExternalOrderId,Platform,ShopId,Version," + ShippingStateSql + ",CASE WHEN EXISTS(SELECT 1 FROM ShippingProfiles p WHERE p.OrderId=SalesOrders.Id) THEN 1 ELSE 0 END FROM SalesOrders WHERE TenantId=$t AND Id=$id", Candidate, ("t", Tenant(db)), ("id", id)).Single();
    public List<ShippingCandidate> ShippingCandidates(string? search = null) => _database.Read(db =>
    {
        if (search?.Length > 100) throw new BusinessRuleException("搜索内容过长。");
        return db.Query("SELECT Id,ExternalOrderId,Platform,ShopId,Version," + ShippingStateSql + ",CASE WHEN EXISTS(SELECT 1 FROM ShippingProfiles p WHERE p.OrderId=SalesOrders.Id) THEN 1 ELSE 0 END FROM SalesOrders WHERE TenantId=$t AND State='Approved' AND NOT EXISTS(SELECT 1 FROM AfterSaleCases c WHERE c.OrderId=SalesOrders.Id AND c.State IN('Open','Approved')) AND CHARINDEX($q,ExternalOrderId)>0" + SecurityContext.ShopFilter("SalesOrders") + " ORDER BY CreatedAt DESC,Id" + db.Limit(200), Candidate, ("t", Tenant(db)), ("q", search?.Trim() ?? ""));
    });
    public OrderShippingPreparation PrepareShipping(string orderId) => _database.Read(db =>
    {
        var row = ShippableOrder(db, orderId); var profile = Profile(db, orderId);
        return new OrderShippingPreparation(CandidateById(db, orderId), profile.HasValue ? new(orderId, PersonalDataView.Address(profile.Value.Address), profile.Value.AllowMerge, row.Version) : null,
            row.Normalized!.Lines.Select(l => new AllocatableLine(l.ExternalLineId, l.InternalSkuCode, l.OrderedQuantity,
                FromQuantity(AllocatedUnits(db, orderId, l.ExternalLineId)), FromQuantity(AllocatedUnits(db, orderId, l.ExternalLineId, true)),
                l.OrderedQuantity - FromQuantity(AllocatedUnits(db, orderId, l.ExternalLineId)))).ToList());
    });
    private const string ShipmentColumns = """
        s.Id,s.Number,s.State,s.Version,(SELECT COUNT(DISTINCT l.OrderId) FROM ShipmentLines l WHERE l.ShipmentId=s.Id),
        (SELECT SUM(l.QuantityUnits) FROM ShipmentLines l WHERE l.ShipmentId=s.Id),s.AddressJson,s.Carrier,s.TrackingNumber,s.CreatedAt,
        CASE WHEN EXISTS(SELECT 1 FROM ShipmentLines l JOIN AfterSaleCases c ON c.OrderId=l.OrderId WHERE l.ShipmentId=s.Id AND c.State IN('Open','Approved')) THEN 1 ELSE 0 END
        """;
    private static ShipmentSummary ShippingSummary(DbRow r) => new(r.GetString(0), r.GetString(1), Enum.Parse<ShipmentState>(r.GetString(2)), r.GetInt64(3),
        r.GetInt32(4), FromQuantity(r.GetInt64(5)), PersonalDataView.Address(FromJson<ShippingAddress>(r.GetString(6))).Recipient, r.GetString(7), r.GetString(8), DateTimeOffset.Parse(r.GetString(9)), r.GetInt64(10) == 1);
    public ShipmentPage Shipments(int page = 1, int pageSize = 20, string? search = null, ShipmentState? state = null) => _database.Read(db =>
    {
        if (page < 1 || page > 1_000_000 || pageSize < 1 || pageSize > 100 || search?.Length > 100 || (state.HasValue && !Enum.IsDefined(state.Value))) throw new BusinessRuleException("出库单查询参数不合法。");
        var filter = "s.TenantId=$t AND ($state IS NULL OR s.State=$state) AND (CHARINDEX($q,s.Number)>0 OR CHARINDEX($q,s.TrackingNumber)>0 OR EXISTS(SELECT 1 FROM ShipmentLines l JOIN SalesOrders o ON o.Id=l.OrderId WHERE l.ShipmentId=s.Id AND CHARINDEX($q,o.ExternalOrderId)>0))" + SecurityContext.ShopFilter("s");
        (string Name, object? Value)[] parameters = [("t", Tenant(db)), ("state", state?.ToString()), ("q", search?.Trim() ?? "")];
        var total = db.Scalar<int>("SELECT COUNT(*) FROM Shipments s WHERE " + filter, parameters);
        return new ShipmentPage(db.Query("SELECT " + ShipmentColumns + " FROM Shipments s WHERE " + filter + " ORDER BY s.CreatedAt DESC,s.Id" + db.Page, ShippingSummary,
            [.. parameters, ("limit", pageSize), ("offset", (page - 1) * pageSize)]), total, page, pageSize);
    });
    public ShipmentDetails ShipmentDetails(string id) => _database.Read(db =>
    {
        var row = Shipment(db, id); var summary = db.Query("SELECT " + ShipmentColumns + " FROM Shipments s WHERE s.Id=$id", ShippingSummary, ("id", id)).Single();
        var lines = db.Query("SELECT l.Id,l.OrderId,o.ExternalOrderId,l.ExternalLineId,l.SnapshotJson,l.QuantityUnits FROM ShipmentLines l JOIN SalesOrders o ON o.Id=l.OrderId WHERE l.ShipmentId=$id ORDER BY o.ExternalOrderId,l.ExternalLineId",
            r => new ShipmentLineView(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), FromJson<NormalizedLine>(r.GetString(4)).InternalSkuCode, FromQuantity(r.GetInt64(5))), ("id", id));
        var checks = db.Query("SELECT c.SkuId,s.Code,s.Name,c.RequiredUnits,c.VerifiedUnits FROM ShipmentChecks c JOIN Skus s ON s.Id=c.SkuId AND s.TenantId=$t WHERE c.ShipmentId=$id ORDER BY s.Code",
            r => new ShipmentCheckView(r.GetString(0), r.GetString(1), r.GetString(2), FromQuantity(r.GetInt64(3)), FromQuantity(r.GetInt64(4))), ("t", Tenant(db)), ("id", id));
        var history = db.Query("SELECT Id,Action,Actor,Reason,DataJson,CreatedAt FROM ShipmentHistory WHERE ShipmentId=$id ORDER BY Id DESC", r => new WarehouseHistory(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), DateTimeOffset.Parse(r.GetString(5))), ("id", id));
        return PersonalDataView.Details(new ShipmentDetails(summary, row.Address, lines, checks, history, DeliveryList(db, id)));
    });
    private static List<DeliveryJobView> DeliveryList(DbSession db, string? shipmentId) => db.Query("""
        SELECT j.Id,j.ShipmentId,s.Number,j.OrderId,o.ExternalOrderId,j.State,j.Version,j.Attempts,j.LastError,j.Receipt,j.NextAttemptAt
        FROM DeliveryJobs j JOIN Shipments s ON s.Id=j.ShipmentId JOIN SalesOrders o ON o.Id=j.OrderId
        WHERE s.TenantId=$t AND ($ship IS NULL OR j.ShipmentId=$ship) 
        """ + SecurityContext.ShopFilter("o") + " ORDER BY j.CreatedAt DESC,j.Id" + db.Limit(200), r => new DeliveryJobView(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetInt64(6), r.GetInt32(7), r.GetString(8), r.GetString(9), r.IsDBNull(10) ? null : DateTimeOffset.Parse(r.GetString(10))), ("t", Tenant(db)), ("ship", shipmentId));
    public List<DeliveryJobView> Deliveries() => _database.Read(db => DeliveryList(db, null).Select(PersonalDataView.Delivery).ToList());
    public List<BarcodeRow> Barcodes() => _database.Read(db => db.Query("SELECT b.Barcode,b.SkuId,s.Code,b.Version FROM SkuBarcodes b JOIN Skus s ON s.TenantId=b.TenantId AND s.Id=b.SkuId WHERE b.TenantId=$t ORDER BY s.Code,b.Barcode", r => new BarcodeRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt64(3)), ("t", Tenant(db))));
    public WarehouseAudit AuditWarehouse() => _database.Read(AuditWarehouseCore);
    private static WarehouseAudit AuditWarehouseCore(DbSession db)
    {
        var problems = new List<string>(); var ids = db.Query("SELECT Id FROM Shipments WHERE TenantId=$t", r => r.GetString(0), ("t", Tenant(db))); var shipped = 0;
        foreach (var id in ids)
        {
            var row = Shipment(db, id); var lines = Allocations(db, id);
            if (lines.Count == 0) problems.Add(row.Number + "：出库单没有明细。");
            var mismatches = db.Scalar<int>("""
                WITH expected AS (SELECT sc.SkuId,SUM(sc.QuantityUnits) Units FROM ShipmentComponents sc JOIN ShipmentLines l ON l.Id=sc.ShipmentLineId WHERE l.ShipmentId=$id GROUP BY sc.SkuId),
                keys AS (SELECT SkuId FROM expected UNION SELECT SkuId FROM ShipmentChecks WHERE ShipmentId=$id)
                SELECT COUNT(*) FROM keys k LEFT JOIN expected e ON e.SkuId=k.SkuId LEFT JOIN ShipmentChecks c ON c.SkuId=k.SkuId AND c.ShipmentId=$id
                WHERE COALESCE(e.Units,0)<>COALESCE(c.RequiredUnits,0)
                """, ("id", id));
            if (mismatches > 0) problems.Add(row.Number + "：复核需求与组件不符。");
            var movement = db.Scalar<long>("SELECT COALESCE(SUM(QuantityUnits),0) FROM ShipmentMovements WHERE ShipmentId=$id", ("id", id));
            if (row.State == ShipmentState.Shipped)
            {
                shipped++; var required = db.Scalar<long>("SELECT SUM(RequiredUnits) FROM ShipmentChecks WHERE ShipmentId=$id", ("id", id));
                var hand = db.Scalar<long>("SELECT -COALESCE(SUM(OnHandDelta),0) FROM InventoryLedger WHERE ShipmentId=$id AND Action='Ship'", ("id", id));
                var reserved = db.Scalar<long>("SELECT -COALESCE(SUM(ReservedDelta),0) FROM InventoryLedger WHERE ShipmentId=$id AND Action='Ship'", ("id", id));
                if (required != movement || required != hand || required != reserved) problems.Add(row.Number + "：出库组件、扣账及流水不符。");
                var perSkuMismatch = db.Scalar<int>("""
                    WITH keys AS (SELECT SkuId FROM ShipmentChecks WHERE ShipmentId=$id UNION SELECT SkuId FROM ShipmentMovements WHERE ShipmentId=$id UNION SELECT SkuId FROM InventoryLedger WHERE ShipmentId=$id AND Action='Ship')
                    SELECT COUNT(*) FROM keys k WHERE
                    COALESCE((SELECT RequiredUnits FROM ShipmentChecks WHERE ShipmentId=$id AND SkuId=k.SkuId),0)<>COALESCE((SELECT SUM(QuantityUnits) FROM ShipmentMovements WHERE ShipmentId=$id AND SkuId=k.SkuId),0)
                    OR COALESCE((SELECT RequiredUnits FROM ShipmentChecks WHERE ShipmentId=$id AND SkuId=k.SkuId),0)<>-COALESCE((SELECT SUM(OnHandDelta) FROM InventoryLedger WHERE ShipmentId=$id AND SkuId=k.SkuId AND Action='Ship'),0)
                    OR COALESCE((SELECT RequiredUnits FROM ShipmentChecks WHERE ShipmentId=$id AND SkuId=k.SkuId),0)<>-COALESCE((SELECT SUM(ReservedDelta) FROM InventoryLedger WHERE ShipmentId=$id AND SkuId=k.SkuId AND Action='Ship'),0)
                    """, ("id", id));
                if (perSkuMismatch > 0) problems.Add(row.Number + "：逐 SKU 出库数量与库存流水不符。");
                if (db.Scalar<int>("SELECT COUNT(*) FROM ShipmentChecks WHERE ShipmentId=$id AND VerifiedUnits<>RequiredUnits", ("id", id)) > 0 || row.TrackingNumber == "") problems.Add(row.Number + "：已出库但复核或运单缺失。");
                if (db.Scalar<int>("SELECT COUNT(*) FROM DeliveryJobs WHERE ShipmentId=$id", ("id", id)) != lines.Select(l => l.OrderId).Distinct().Count()) problems.Add(row.Number + "：原订单回传任务数量不符。");
            }
            else if (movement != 0) problems.Add(row.Number + "：未出库单存在库存扣账。");
        }
        var overAllocated = db.Scalar<int>("""
            SELECT COUNT(*) FROM OrderLines ol WHERE (SELECT COALESCE(SUM(l.QuantityUnits),0) FROM ShipmentLines l JOIN Shipments s ON s.Id=l.ShipmentId WHERE l.OrderId=ol.OrderId AND l.ExternalLineId=ol.ExternalLineId AND s.State NOT IN('Voided','Superseded'))>ol.QuantityUnits
            """);
        if (overAllocated > 0) problems.Add("存在订单行超额配单。");
        var consumedMismatch = db.Scalar<int>("""
            SELECT COUNT(*) FROM Reservations r WHERE r.TenantId=$t AND r.ConsumedUnits<>(SELECT COALESCE(SUM(m.QuantityUnits),0) FROM ShipmentMovements m JOIN Shipments s ON s.Id=m.ShipmentId WHERE m.OrderId=r.OrderId AND m.SkuId=r.SkuId AND s.State='Shipped')
            """, ("t", Tenant(db)));
        if (consumedMismatch > 0) problems.Add("订单预占消耗量与出库记录不符。");
        return new(problems.Count == 0, ids.Count, shipped, problems);
    }
}
