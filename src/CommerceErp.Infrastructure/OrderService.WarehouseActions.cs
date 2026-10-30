using CommerceErp.Core;
using static CommerceErp.Infrastructure.CommerceDatabase;
using static CommerceErp.Core.BusinessUnits;

namespace CommerceErp.Infrastructure;

public sealed partial class OrderService
{
    public WarehouseResult StartPicking(string id, WarehouseActionRequest request) => WarehouseCommand(request.OperationId, "StartPicking", new { id, request }, request.Actor, db =>
    {
        var row = Shipment(db, id); ShipmentVersionCheck(row, request.ExpectedVersion); RequireState(row, ShipmentState.Draft);
        foreach (var orderId in Allocations(db, id).Select(l => l.OrderId).Distinct()) _ = ShippableOrder(db, orderId);
        Transition(db, row, ShipmentState.Picking, "StartPicking", request.Actor, "开始拣货与扫码复核", new { });
        return ShippingResult(db, id, "已进入拣货复核。按实际拣出数量扫描实物 SKU 条码，数量齐全后封箱。");
    });
    public WarehouseResult ScanShipment(string id, ScanShipmentRequest request) => WarehouseCommand(request.OperationId, "ScanShipment", new { id, request }, request.Actor, db =>
    {
        GuardShipmentAfterSale(db, id);
        var row = Shipment(db, id); ShipmentVersionCheck(row, request.ExpectedVersion); RequireState(row, ShipmentState.Picking);
        var units = Quantity(request.Quantity); if (units <= 0) throw new BusinessRuleException("扫描数量须大于 0。");
        var sku = db.Scalar<string>("SELECT SkuId FROM SkuBarcodes WHERE TenantId=$t AND Barcode=$b", ("t", Tenant(db)), ("b", request.Barcode));
        if (sku is null) throw new BusinessRuleException("条码未登记。可使用内部 SKU 编码，或先登记实物条码。");
        if (db.Execute("UPDATE ShipmentChecks SET VerifiedUnits=VerifiedUnits+$q WHERE ShipmentId=$id AND SkuId=$sku AND RequiredUnits-VerifiedUnits >= $q",
            ("q", units), ("id", id), ("sku", sku)) != 1) throw new BusinessRuleException("扫描商品不属于此包裹，或数量超过尚未复核的数量。");
        Transition(db, row, ShipmentState.Picking, "Scan", request.Actor, "扫码复核", new { request.Barcode, sku, request.Quantity });
        return ShippingResult(db, id, "扫码已记录。");
    });
    public WarehouseResult ResetVerification(string id, WarehouseActionRequest request) => WarehouseCommand(request.OperationId, "ResetVerification", new { id, request }, request.Actor, db =>
    {
        Reason(request.Reason); var row = Shipment(db, id); ShipmentVersionCheck(row, request.ExpectedVersion); RequireState(row, ShipmentState.Picking);
        var previous = db.Query("SELECT SkuId,VerifiedUnits FROM ShipmentChecks WHERE ShipmentId=$id", r => new { skuId = r.GetString(0), verifiedUnits = r.GetInt64(1) }, ("id", id));
        db.Execute("UPDATE ShipmentChecks SET VerifiedUnits=0 WHERE ShipmentId=$id", ("id", id));
        Transition(db, row, row.State, "ResetVerification", request.Actor, request.Reason, previous);
        return ShippingResult(db, id, "复核数量已归零，原扫描与重置原因保留在操作轨迹中，请重新扫描整个包裹。");
    });
    public WarehouseResult PackShipment(string id, WarehouseActionRequest request) => WarehouseCommand(request.OperationId, "PackShipment", new { id, request }, request.Actor, db =>
    {
        GuardShipmentAfterSale(db, id);
        var row = Shipment(db, id); ShipmentVersionCheck(row, request.ExpectedVersion); RequireState(row, ShipmentState.Picking);
        if (db.Scalar<int>("SELECT COUNT(*) FROM ShipmentChecks WHERE ShipmentId=$id AND VerifiedUnits<>RequiredUnits", ("id", id)) != 0)
            throw new BusinessRuleException("仍有商品未完成扫码复核，不能封箱。");
        Transition(db, row, ShipmentState.Packed, "Pack", request.Actor, "复核齐全，封箱完成", new { });
        return ShippingResult(db, id, "包裹已封箱，录入实际承运商和运单号后可确认出库。");
    });
    public WarehouseResult SaveWaybill(string id, WaybillRequest request) => WarehouseCommand(request.OperationId, "Waybill", new { id, request }, request.Actor, db =>
    {
        GuardShipmentAfterSale(db, id);
        Reason(request.Reason); var row = Shipment(db, id); ShipmentVersionCheck(row, request.ExpectedVersion); RequireState(row, ShipmentState.Draft, ShipmentState.Picking, ShipmentState.Packed);
        var carrier = request.Carrier?.Trim() ?? ""; var tracking = request.TrackingNumber?.Trim() ?? "";
        if (carrier.Length is < 1 or > 40 || carrier.Any(char.IsControl) || tracking.Length is < 6 or > 64 || tracking.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_'))
            throw new BusinessRuleException("承运商必填；运单号须为 6–64 位字母、数字、横线或下划线。");
        if (db.Scalar<int>("SELECT COUNT(*) FROM Shipments WHERE TenantId=$t AND Id<>$id AND Carrier=$c AND TrackingNumber=$n AND State NOT IN('Voided','Superseded')",
            ("t", Tenant(db)), ("id", id), ("c", carrier), ("n", tracking)) > 0) throw new ConflictException("同一承运商的运单号已被另一有效包裹使用。");
        db.Execute("UPDATE Shipments SET Carrier=$c,TrackingNumber=$n WHERE Id=$id", ("c", carrier), ("n", tracking), ("id", id));
        Transition(db, row, row.State, "Waybill", request.Actor, request.Reason, new { previousCarrier = row.Carrier, previousNumber = row.TrackingNumber, carrier, tracking });
        return ShippingResult(db, id, "运单已登记。此功能不向物流商购买或申请电子面单。");
    });
    public WarehouseResult VoidShipment(string id, WarehouseActionRequest request) => WarehouseCommand(request.OperationId, "VoidShipment", new { id, request }, request.Actor, db =>
    {
        Reason(request.Reason); var row = Shipment(db, id); ShipmentVersionCheck(row, request.ExpectedVersion); RequireState(row, ShipmentState.Draft, ShipmentState.Picking, ShipmentState.Packed);
        Transition(db, row, ShipmentState.Voided, "Void", request.Actor, request.Reason, new { });
        foreach (var order in Allocations(db, id).Select(l => l.OrderId).Distinct()) TouchOrder(db, order, "VoidShipment", request.Actor, request.Reason);
        return ShippingResult(db, id, "出库单已作废，配单数量可重新分配。原订单预占保留；已拣实物请归还库位。");
    });
    public WarehouseResult DispatchShipment(string id, WarehouseActionRequest request) => WarehouseCommand(request.OperationId, "DispatchShipment", new { id, request }, request.Actor, db =>
    {
        Reason(request.Reason); var row = Shipment(db, id);
        if (row.State == ShipmentState.Shipped) return ShippingResult(db, id, "该包裹已出库，没有重复扣账。") with { Replayed = true };
        ShipmentVersionCheck(row, request.ExpectedVersion); RequireState(row, ShipmentState.Packed);
        if (row.Carrier == "" || row.TrackingNumber == "") throw new BusinessRuleException("请先登记实际承运商和运单号。");
        if (db.Scalar<int>("SELECT COUNT(*) FROM ShipmentChecks WHERE ShipmentId=$id AND VerifiedUnits<>RequiredUnits", ("id", id)) > 0) throw new BusinessRuleException("复核数量不完整。");
        var lines = Allocations(db, id); var orders = lines.Select(l => l.OrderId).Distinct().Select(o => ShippableOrder(db, o)).ToList();
        var components = db.Query("SELECT l.OrderId,c.SkuId,SUM(c.QuantityUnits) FROM ShipmentComponents c JOIN ShipmentLines l ON l.Id=c.ShipmentLineId WHERE l.ShipmentId=$id GROUP BY l.OrderId,c.SkuId ORDER BY l.OrderId,c.SkuId",
            r => (Order: r.GetString(0), Sku: r.GetString(1), Units: r.GetInt64(2)), ("id", id));
        foreach (var item in components)
        {
            if (db.Execute("UPDATE Reservations SET ConsumedUnits=ConsumedUnits+$q WHERE TenantId=$t AND OrderId=$order AND SkuId=$sku AND State='Active' AND QuantityUnits-ConsumedUnits >= $q",
                ("q", item.Units), ("t", Tenant(db)), ("order", item.Order), ("sku", item.Sku)) != 1) throw new ConflictException("订单有效预占不足，整张出库单未扣账。");
            db.Execute("INSERT INTO ShipmentMovements VALUES($id,$order,$sku,$q)", ("id", id), ("order", item.Order), ("sku", item.Sku), ("q", item.Units));
        }
        foreach (var item in components.GroupBy(c => c.Sku).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var units = item.Sum(c => c.Units);
            if (db.Execute("UPDATE InventoryBalances SET OnHandUnits=OnHandUnits-$q,ReservedUnits=ReservedUnits-$q,Version=Version+1 WHERE TenantId=$t AND OwnerId=$o AND WarehouseId=$w AND SkuId=$s AND OnHandUnits >= $q AND ReservedUnits >= $q",
                ("q", units), ("t", Tenant(db)), ("o", OwnerOf(db)), ("w", WarehouseOf(db)), ("s", item.Key)) != 1) throw new ConflictException("库存余额不足或不一致，整张出库单未扣账。");
            Ledger(db, item.Key, request.OperationId, "Ship", orders.Count == 1 ? orders[0].Id : null, -units, -units, row.Number + "：" + request.Reason);
            db.Execute("UPDATE InventoryLedger SET ShipmentId=$id WHERE TenantId=$t AND OperationId=$op AND Action='Ship' AND SkuId=$sku",
                ("id", id), ("t", Tenant(db)), ("op", request.OperationId), ("sku", item.Key));
            _fault?.Invoke("warehouse-after-stock");
        }
        CaptureShipmentCosts(db, id);
        Transition(db, row, ShipmentState.Shipped, "Dispatch", request.Actor, request.Reason, new { row.Carrier, row.TrackingNumber });
        foreach (var order in orders)
        {
            TouchOrder(db, order.Id, "Dispatch", request.Actor, row.Number + "：" + request.Reason);
            var jobId = Guid.NewGuid().ToString("N");
            var notification = new ShipmentNotification(jobId, row.Id, row.Number, order.Source.Platform, order.Source.ShopId, order.Source.ExternalOrderId, row.Carrier, row.TrackingNumber,
                order.Normalized!.Lines.Any(l => lines.Where(a => a.OrderId == order.Id && a.ExternalLineId == l.ExternalLineId).Sum(a => a.Units) != Quantity(l.OrderedQuantity)), orders.Count > 1,
                lines.Where(l => l.OrderId == order.Id).Select(l => new ShipmentNotificationLine(l.ExternalLineId, l.Snapshot.PlatformProductId, l.Snapshot.PlatformSkuId, FromQuantity(l.Units))).ToList());
            db.Execute("INSERT INTO DeliveryJobs(Id,ShipmentId,OrderId,PayloadJson,State,LastError,CreatedAt,UpdatedAt) VALUES($id,$ship,$order,$payload,'Blocked',$error,$now,$now)",
                ("id", jobId), ("ship", id), ("order", order.Id), ("payload", Json(notification)), ("error", "未配置已授权的平台发货连接器，未向平台发送。"), ("now", Now()));
        }
        return ShippingResult(db, id, "出库已扣减实物与预占，并生成每个原订单的回传任务。平台尚未确认发货，请查看回传队列。");
    });
    public WarehouseResult SaveBarcode(SaveBarcodeRequest request) => WarehouseCommand(request.OperationId, "SaveBarcode", request, request.Actor, db =>
    {
        Reason(request.Reason); var code = request.Barcode?.Trim() ?? "";
        if (code.Length is < 1 or > 100 || code.Any(char.IsControl)) throw new BusinessRuleException("条码须为 1–100 个非控制字符。");
        var sku = ReadCatalog(db).Skus.SingleOrDefault(s => s.Id == request.SkuId);
        if (sku is null || !sku.Enabled || sku.Components.Count != 0) throw new BusinessRuleException("条码只能登记到启用的实物 SKU。");
        var current = db.Query("SELECT SkuId,Version FROM SkuBarcodes WHERE TenantId=$t AND Barcode=$b", r => (Sku: r.GetString(0), Version: r.GetInt64(1)), ("t", Tenant(db)), ("b", code)).SingleOrDefault();
        if (current.Version != request.ExpectedVersion) throw new ConflictException("条码版本已变化，请刷新。");
        if (current.Sku is not null && current.Sku != request.SkuId) throw new ConflictException("已有条码不可重指向另一 SKU，请核实实物标签。");
        db.Upsert("UPDATE SkuBarcodes SET Version=Version+1 WHERE TenantId=$t AND Barcode=$b", "INSERT INTO SkuBarcodes VALUES($t,$b,$sku,1)", ("t", Tenant(db)), ("b", code), ("sku", sku.Id));
        db.Execute("INSERT INTO WarehouseConfigurationHistory(Entity,EntityId,Actor,Reason,DataJson,CreatedAt) VALUES('Barcode',$id,$actor,$reason,$data,$now)",
            ("id", code), ("actor", request.Actor), ("reason", request.Reason), ("data", Json(new { code, sku.Id })), ("now", Now()));
        return new(code, current.Version + 1, "BarcodeSaved", false, "条码已登记，可用于扫描枪输入复核。", []);
    });
    public WarehouseResult RetryDelivery(string id, WarehouseActionRequest request) => WarehouseCommand(request.OperationId, "RetryDelivery", new { id, request }, request.Actor, db =>
    {
        Reason(request.Reason);
        var job = db.Query("SELECT Version,State,LeaseUntil,ShipmentId FROM DeliveryJobs WHERE Id=$id", r => (Version: r.GetInt64(0), State: r.GetString(1), Lease: r.IsDBNull(2) ? "" : r.GetString(2), Shipment: r.GetString(3)), ("id", id)).SingleOrDefault();
        if (job.Version == 0) throw new BusinessRuleException("回传任务不存在。");
        if (job.State == "Confirmed") return new(id, job.Version, job.State, true, "平台已确认，未重新发送。", []);
        if (job.State=="Pending")throw new ConflictException("任务已排队，请等待后台处理。");
        if (job.Version != request.ExpectedVersion || (job.State == "Processing" && string.CompareOrdinal(job.Lease, Now()) > 0)) throw new ConflictException("任务已变化或正在处理中，请刷新。");
        db.Execute("UPDATE DeliveryJobs SET State='Pending',CycleAttempts=0,Version=Version+1,NextAttemptAt=$now,LeaseToken=NULL,LeaseUntil=NULL,UpdatedAt=$now WHERE Id=$id", ("now", Now()), ("id", id));
        ShippingHistory(db, job.Shipment, "RetryDelivery", request.Actor, request.Reason, new { jobId = id });
        return new(id, job.Version + 1, "Pending", false, "已加入回传队列。后台先回查结果；未配置连接器时仍会标记阻断。", []);
    });
}
