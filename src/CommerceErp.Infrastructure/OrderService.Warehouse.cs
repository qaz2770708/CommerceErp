using CommerceErp.Core;
using static CommerceErp.Infrastructure.CommerceDatabase;
using static CommerceErp.Core.BusinessUnits;

namespace CommerceErp.Infrastructure;

public sealed partial class OrderService
{
    private sealed record ShippingRecord(string Id, string Number, ShipmentState State, long Version, Platform Platform, string ShopId,
        ShippingAddress Address, string AddressHash, string Carrier, string TrackingNumber);
    private sealed record AllocationRecord(string Id, string OrderId, string ExternalLineId, long Units, NormalizedLine Snapshot);
    private static void Actor(string? actor) { if (string.IsNullOrWhiteSpace(actor) || actor.Length > 80 || actor.Any(char.IsControl)) throw new BusinessRuleException("请填写 1–80 字的操作人。"); }
    private WarehouseResult WarehouseCommand(string operationId, string kind, object request, string actor, Func<DbSession, WarehouseResult> action)
    {
        OperationId(operationId); Actor(actor); var hash = Hash(request);
        return _database.Write(db =>
        {
            var replay = Replay<WarehouseResult>(db, operationId, kind, hash);
            if (replay is not null) return replay with { Replayed = true, Message = "相同仓储操作已处理，没有重复执行。请刷新查看最新状态。" };
            var result = action(db); SaveOperation(db, operationId, kind, hash, result); _fault?.Invoke("warehouse-before-commit"); return result;
        });
    }
    private static ShippingRecord Shipment(DbSession db, string id) => db.Query("SELECT Id,Number,State,Version,Platform,ShopId,AddressJson,AddressHash,Carrier,TrackingNumber FROM Shipments WHERE Id=$id AND TenantId=$t",
        r => new ShippingRecord(r.GetString(0), r.GetString(1), Enum.Parse<ShipmentState>(r.GetString(2)), r.GetInt64(3), Enum.Parse<Platform>(r.GetString(4)), r.GetString(5),
            FromJson<ShippingAddress>(r.GetString(6)), r.GetString(7), r.GetString(8), r.GetString(9)), ("id", id), ("t", Tenant(db))).SingleOrDefault() ?? throw new BusinessRuleException("出库单不存在。");
    private static List<AllocationRecord> Allocations(DbSession db, string id) => db.Query("SELECT Id,OrderId,ExternalLineId,QuantityUnits,SnapshotJson FROM ShipmentLines WHERE ShipmentId=$id ORDER BY OrderId,ExternalLineId",
        r => new AllocationRecord(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt64(3), FromJson<NormalizedLine>(r.GetString(4))), ("id", id));
    private static void ShipmentVersionCheck(ShippingRecord row, long version) { if (row.Version != version) throw new ConflictException("出库单已变化，请刷新后操作。"); }
    private static void RequireState(ShippingRecord row, params ShipmentState[] states) { if (!states.Contains(row.State)) throw new ConflictException($"当前出库单状态 {row.State} 不允许此操作。"); }
    private static void ShippingHistory(DbSession db, string id, string action, string actor, string reason, object data) =>
        db.Execute("INSERT INTO ShipmentHistory(ShipmentId,Action,Actor,Reason,DataJson,CreatedAt) VALUES($id,$action,$actor,$reason,$data,$now)",
            ("id", id), ("action", action), ("actor", actor), ("reason", reason), ("data", Json(data)), ("now", Now()));
    private static void Transition(DbSession db, ShippingRecord row, ShipmentState state, string action, string actor, string reason, object data)
    {
        db.Execute("UPDATE Shipments SET State=$s,Version=Version+1,UpdatedAt=$now WHERE Id=$id", ("s", state.ToString()), ("now", Now()), ("id", row.Id));
        ShippingHistory(db, row.Id, action, actor, reason, data);
        Event(db, "shipment:" + row.Id, row.Version + 1, "WarehouseChanged", new { shipmentId = row.Id, state, action });
    }
    private static WarehouseResult ShippingResult(DbSession db, string id, string message, List<string>? related = null)
    { var row = Shipment(db, id); return new(row.Id, row.Version, row.State.ToString(), false, message, related ?? []); }
    private static void TouchOrder(DbSession db, string id, string action, string actor, string reason)
    {
        var row = Get(db, id); db.Execute("UPDATE SalesOrders SET Version=Version+1,UpdatedAt=$now WHERE Id=$id", ("id", id), ("now", Now()));
        History(db, id, action, row.State.ToString(), row.State.ToString(), actor + "：" + reason); Snapshot(db, id);
        Event(db, id, row.Version + 1, "OrderFulfillmentChanged", new { orderId = id, action });
    }
    private static StoredOrder ShippableOrder(DbSession db, string id)
    {
        GuardAfterSale(db, id);
        var row = Get(db, id);
        if (row.State != ReviewState.Approved || row.Normalized is null || row.Source.PaymentState != PaymentState.Paid || row.Source.FulfillmentState != FulfillmentState.Unfulfilled)
            throw new ConflictException("只有已审核、已付款且来源待履约的订单可以配单出库。");
        return row;
    }
    private static (ShippingAddress Address, string Hash, bool AllowMerge)? Profile(DbSession db, string id) => db.Query(
        "SELECT AddressJson,AddressHash,AllowMerge FROM ShippingProfiles WHERE OrderId=$id", r => ((ShippingAddress Address, string Hash, bool AllowMerge)?)(FromJson<ShippingAddress>(r.GetString(0)), r.GetString(1), r.GetInt64(2) == 1), ("id", id)).SingleOrDefault();
    private static ShippingAddress CleanAddress(ShippingAddress? address)
    {
        if (address is null) throw new BusinessRuleException("请填写收货资料。");
        string Field(string? value, int max, bool optional = false)
        { var s = value?.Trim() ?? ""; if ((!optional && s.Length == 0) || s.Length > max || s.Any(char.IsControl)) throw new BusinessRuleException("收货资料不完整、过长或包含控制字符。"); return s; }
        return new(Field(address.Recipient, 80), Field(address.Phone, 40), Field(address.Province, 80), Field(address.City, 80), Field(address.District, 80), Field(address.Street, 300), Field(address.PostalCode, 20, true));
    }
    private static long AllocatedUnits(DbSession db, string orderId, string lineId, bool shippedOnly = false) => db.Scalar<long>("""
        SELECT COALESCE(SUM(l.QuantityUnits),0) FROM ShipmentLines l JOIN Shipments s ON s.Id=l.ShipmentId
        WHERE l.OrderId=$o AND l.ExternalLineId=$line AND s.State NOT IN('Voided','Superseded') AND ($only=0 OR s.State='Shipped')
        """, ("o", orderId), ("line", lineId), ("only", shippedOnly ? 1 : 0));

    public WarehouseResult SaveShippingProfile(string orderId, SaveShippingProfileRequest request) => WarehouseCommand(request.OperationId, "ShippingProfile", new { orderId, request }, request.Actor, db =>
    {
        Reason(request.Reason); var order = ShippableOrder(db, orderId); Version(order, request.ExpectedVersion);
        if (db.Scalar<long>("SELECT COUNT(*) FROM ShipmentLines l JOIN Shipments s ON s.Id=l.ShipmentId WHERE l.OrderId=$o AND s.State NOT IN('Voided','Superseded')", ("o", orderId)) > 0)
            throw new ConflictException("存在未作废出库单或已出库记录，不能修改收货资料。先处理出库单；已出库订单需走售后流程。");
        var address = CleanAddress(request.Address);
        db.Upsert("UPDATE ShippingProfiles SET AddressJson=$json,AddressHash=$hash,AllowMerge=$merge WHERE OrderId=$o", "INSERT INTO ShippingProfiles VALUES($o,$json,$hash,$merge)",
            ("o", orderId), ("json", Json(address)), ("hash", Hash(address)), ("merge", request.AllowMerge ? 1 : 0));
        db.Execute("INSERT INTO WarehouseConfigurationHistory(Entity,EntityId,Actor,Reason,DataJson,CreatedAt) VALUES('ShippingProfile',$id,$actor,$reason,$data,$now)",
            ("id", orderId), ("actor", request.Actor), ("reason", request.Reason), ("data", Json(new { address, request.AllowMerge })), ("now", Now()));
        TouchOrder(db, orderId, "ShippingProfile", request.Actor, request.Reason);
        return new(orderId, order.Version + 1, "ProfileSaved", false, "收货资料已保存。跨订单合包须每张订单均明确允许合包。", []);
    });

    private static string CreateShipmentCore(DbSession db, List<AllocationLine>? lines, string actor, string reason, bool touchOrders = true)
    {
        if (lines is null || lines.Count is < 1 or > 1000 || lines.Any(l => l is null)) throw new BusinessRuleException("出库单须包含 1–1000 条有效订单行。");
        if (lines.Select(l => (l.OrderId, l.ExternalLineId)).Distinct().Count() != lines.Count) throw new BusinessRuleException("出库单内同一订单行不能重复，请合并数量。");
        var orders = lines.Select(l => l.OrderId).Distinct().Select(id => ShippableOrder(db, id)).ToList();
        if (orders.Count > 20) throw new BusinessRuleException("单包最多合并 20 张订单。");
        var first = orders[0]; var profile = Profile(db, first.Id) ?? throw new BusinessRuleException("请先保存每张订单的收货资料。");
        foreach (var order in orders)
        {
            foreach (var line in lines.Where(l => l.OrderId == order.Id)) Version(order, line.ExpectedOrderVersion);
            var current = Profile(db, order.Id) ?? throw new BusinessRuleException("订单缺少收货资料。");
            if (order.Source.Platform != first.Source.Platform || order.Source.ShopId != first.Source.ShopId || current.Hash != profile.Hash)
                throw new BusinessRuleException("合单须同平台、同店铺、同货主仓库，且收件人、电话和完整地址完全一致。");
            if (orders.Count > 1 && !current.AllowMerge) throw new BusinessRuleException("存在未允许跨订单合包的订单。");
        }
        var id = Guid.NewGuid().ToString("N"); var number = "WH-" + DateTime.UtcNow.ToString("yyyyMMdd") + "-" + id[..10].ToUpperInvariant(); var now = Now();
        db.Execute("INSERT INTO Shipments(Id,Number,TenantId,OwnerId,WarehouseId,Platform,ShopId,AddressJson,AddressHash,State,CreatedAt,UpdatedAt) VALUES($id,$n,$t,$o,$w,$p,$shop,$address,$hash,'Draft',$now,$now)",
            ("id", id), ("n", number), ("t", Tenant(db)), ("o", OwnerOf(db)), ("w", WarehouseOf(db)), ("p", first.Source.Platform.ToString()), ("shop", first.Source.ShopId), ("address", Json(profile.Address)), ("hash", profile.Hash), ("now", now));
        foreach (var item in lines)
        {
            var units = Quantity(item.Quantity); if (units == 0) throw new BusinessRuleException("配单数量须大于 0。");
            var order = orders.Single(o => o.Id == item.OrderId);
            var line = order.Normalized!.Lines.SingleOrDefault(l => l.ExternalLineId == item.ExternalLineId) ?? throw new BusinessRuleException("原订单行不存在。");
            if (units > Quantity(line.OrderedQuantity) - AllocatedUnits(db, item.OrderId, item.ExternalLineId)) throw new ConflictException("配单数量超过该订单行的未分配数量，请刷新。");
            var lineId = Guid.NewGuid().ToString("N");
            db.Execute("INSERT INTO ShipmentLines VALUES($id,$ship,$order,$line,$q,$snapshot)", ("id", lineId), ("ship", id), ("order", item.OrderId), ("line", item.ExternalLineId), ("q", units), ("snapshot", Json(line)));
            foreach (var component in line.StockRequirements)
            {
                var quantity = Quantity(component.Quantity * item.Quantity / line.OrderedQuantity);
                if (quantity <= 0) throw new BusinessRuleException("拆分后的组件数量必须大于 0 且精确到 0.001，请调整拆分数量。");
                db.Execute("INSERT INTO ShipmentComponents VALUES($line,$sku,$q)", ("line", lineId), ("sku", component.InternalSkuId), ("q", quantity));
                db.Upsert("UPDATE ShipmentChecks SET RequiredUnits=RequiredUnits+$q WHERE ShipmentId=$id AND SkuId=$sku", "INSERT INTO ShipmentChecks(ShipmentId,SkuId,RequiredUnits) VALUES($id,$sku,$q)", ("id", id), ("sku", component.InternalSkuId), ("q", quantity));
            }
        }
        ShippingHistory(db, id, "Create", actor, reason, new { orderIds = orders.Select(o => o.Id).ToList(), lines });
        Event(db, "shipment:" + id, 1, "WarehouseCreated", new { shipmentId = id, number });
        if (touchOrders) foreach (var order in orders) TouchOrder(db, order.Id, "Allocate", actor, number + "：" + reason);
        return id;
    }
    public WarehouseResult CreateShipment(CreateShipmentRequest request) => WarehouseCommand(request.OperationId, "CreateShipment", request, request.Actor, db =>
    { Reason(request.Reason); var id = CreateShipmentCore(db, request.Lines, request.Actor, request.Reason); return ShippingResult(db, id, "出库单已生成，库存预占仍归属原订单；尚未扣减实物库存。"); });

    public WarehouseResult SplitShipment(string id, SplitShipmentRequest request) => WarehouseCommand(request.OperationId, "SplitShipment", new { id, request }, request.Actor, db =>
    {
        Reason(request.Reason); var row = Shipment(db, id); ShipmentVersionCheck(row, request.ExpectedVersion); RequireState(row, ShipmentState.Draft);
        if (row.TrackingNumber != "") throw new ConflictException("已录入运单的出库单不能拆单，请作废后重新配单。");
        if (request.Lines is null || request.Lines.Count == 0 || request.Lines.Any(l => l is null) || request.Lines.Select(l => l.LineId).Distinct().Count() != request.Lines.Count)
            throw new BusinessRuleException("请选择不重复的拆分明细。");
        var source = Allocations(db, id); var moved = new Dictionary<string, long>();
        foreach (var item in request.Lines)
        {
            var line = source.SingleOrDefault(l => l.Id == item.LineId) ?? throw new BusinessRuleException("拆分行不属于本出库单。");
            var units = Quantity(item.Quantity); if (units <= 0 || units > line.Units) throw new BusinessRuleException("拆出数量须大于 0 且不能超过原行数量。"); moved.Add(item.LineId, units);
        }
        if (source.All(l => moved.GetValueOrDefault(l.Id) == l.Units)) throw new BusinessRuleException("拆单后两个包裹都必须包含商品。");
        var part1 = new List<AllocationLine>(); var part2 = new List<AllocationLine>();
        foreach (var line in source)
        {
            var version = ShippableOrder(db, line.OrderId).Version; var units = moved.GetValueOrDefault(line.Id);
            if (units > 0) part1.Add(new(line.OrderId, line.ExternalLineId, FromQuantity(units), version));
            if (units < line.Units) part2.Add(new(line.OrderId, line.ExternalLineId, FromQuantity(line.Units - units), version));
        }
        Transition(db, row, ShipmentState.Superseded, "Split", request.Actor, request.Reason, request.Lines);
        var a = CreateShipmentCore(db, part1, request.Actor, "拆单：" + request.Reason, false); var b = CreateShipmentCore(db, part2, request.Actor, "拆单：" + request.Reason, false);
        foreach (var target in new[] { a, b }) db.Execute("INSERT INTO ShipmentRelations VALUES($from,$to,'Split')", ("from", id), ("to", target));
        foreach (var orderId in source.Select(l => l.OrderId).Distinct()) TouchOrder(db, orderId, "Split", request.Actor, request.Reason);
        return ShippingResult(db, a, "原出库单已被两张新出库单替代，订单行数量和预占总量保持不变。", [a, b]);
    });
    public WarehouseResult MergeShipments(MergeShipmentsRequest request) => WarehouseCommand(request.OperationId, "MergeShipments", request, request.Actor, db =>
    {
        Reason(request.Reason);
        if (request.Shipments is null || request.Shipments.Count is < 2 or > 20 || request.Shipments.Any(s => s is null) || request.Shipments.Select(s => s.Id).Distinct().Count() != request.Shipments.Count)
            throw new BusinessRuleException("请选择 2–20 张不同的待拣货出库单。");
        var records = request.Shipments.Select(s => Shipment(db, s.Id)).ToList();
        foreach (var row in records)
        {
            ShipmentVersionCheck(row, request.Shipments.Single(s => s.Id == row.Id).Version); RequireState(row, ShipmentState.Draft);
            if (row.TrackingNumber != "" || row.AddressHash != records[0].AddressHash || row.Platform != records[0].Platform || row.ShopId != records[0].ShopId)
                throw new BusinessRuleException("合单要求未录入运单、同平台店铺及完全相同的收货资料。");
        }
        var lines = records.SelectMany(r => Allocations(db, r.Id)).GroupBy(l => (l.OrderId, l.ExternalLineId))
            .Select(g => new AllocationLine(g.Key.OrderId, g.Key.ExternalLineId, FromQuantity(g.Sum(l => l.Units)), ShippableOrder(db, g.Key.OrderId).Version)).ToList();
        foreach (var row in records) Transition(db, row, ShipmentState.Superseded, "Merge", request.Actor, request.Reason, request.Shipments);
        var id = CreateShipmentCore(db, lines, request.Actor, "合单：" + request.Reason, false);
        foreach (var row in records) db.Execute("INSERT INTO ShipmentRelations VALUES($from,$to,'Merge')", ("from", row.Id), ("to", id));
        foreach (var orderId in lines.Select(l => l.OrderId).Distinct()) TouchOrder(db, orderId, "Merge", request.Actor, request.Reason);
        return ShippingResult(db, id, "合单完成，原出库单已保留并标记为被替代。", [id]);
    });
}
