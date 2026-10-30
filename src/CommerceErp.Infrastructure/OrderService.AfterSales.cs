using CommerceErp.Core;
using static CommerceErp.Infrastructure.CommerceDatabase;
using static CommerceErp.Core.BusinessUnits;

namespace CommerceErp.Infrastructure;

public sealed partial class OrderService
{
    private sealed record AfterSaleRecord(string Id, string OrderId, AfterSaleKind Kind, AfterSaleState State, long Version,
        long RefundCents, RefundTrackingState RefundState, string Platform, string ShopId, string RefundReference);
    private static AfterSaleRecord AfterCase(DbSession db, string id) => db.Query("""
        SELECT Id,OrderId,Kind,State,Version,RefundCents,RefundState,Platform,ShopId,RefundReference FROM AfterSaleCases WHERE Id=$id AND TenantId=$t
        """, r => new AfterSaleRecord(r.GetString(0), r.GetString(1), Enum.Parse<AfterSaleKind>(r.GetString(2)), Enum.Parse<AfterSaleState>(r.GetString(3)),
            r.GetInt64(4), r.GetInt64(5), Enum.Parse<RefundTrackingState>(r.GetString(6)), r.GetString(7), r.GetString(8), r.GetString(9)),
        ("id", id), ("t", Tenant(db))).SingleOrDefault() ?? throw new BusinessRuleException("售后单不存在。");
    private static void AfterVersion(AfterSaleRecord c, long expected) { if (c.Version != expected) throw new ConflictException("售后单已变化，请刷新后操作。"); }
    private static string AfterText(string? value, string label, int max, bool optional = false)
    {
        var text = value?.Trim() ?? "";
        if ((!optional && text.Length == 0) || text.Length > max || text.Any(char.IsControl)) throw new BusinessRuleException($"{label}须为{(optional ? "0" : "1")}–{max}个非控制字符。");
        return text;
    }
    private static long RefundMoney(decimal amount)
    {
        if (amount <= 0 || amount > 1_000_000_000m || decimal.Round(amount, 2) != amount) throw new BusinessRuleException("退款金额须大于 0、不超过 10 亿，最多两位小数。");
        return Money(amount);
    }
    private AfterSaleResult AfterCommand(string operationId, string kind, object request, string actor, string reason, Func<DbSession, AfterSaleResult> action)
    {
        OperationId(operationId); Actor(actor); Reason(reason); string hash;
        try { hash = Hash(request); }
        catch (System.Text.Json.JsonException) { throw new BusinessRuleException("售后请求包含不合法的枚举或数据格式。"); }
        return _database.Write(db =>
        {
            var replay = Replay<AfterSaleResult>(db, operationId, kind, hash);
            if (replay is not null) return replay with { Replayed = true, Message = "相同售后操作已处理，没有重复入账。请刷新查看当前状态。" };
            var result = action(db); SaveOperation(db, operationId, kind, hash, result); _fault?.Invoke("aftersale-before-commit"); return result;
        });
    }
    private static AfterSaleResult AfterResult(DbSession db, string id, string message, bool replayed = false)
    { var c = AfterCase(db, id); return new(id, c.Version, c.State, replayed, message); }
    private static void AfterHistory(DbSession db, string id, string action, string actor, string reason, object data) =>
        db.Execute("INSERT INTO AfterSaleHistory(CaseId,Action,Actor,Reason,DataJson,CreatedAt) VALUES($id,$action,$actor,$reason,$data,$now)",
            ("id", id), ("action", action), ("actor", actor), ("reason", reason), ("data", Json(data)), ("now", Now()));
    private static void AfterTransition(DbSession db, AfterSaleRecord c, AfterSaleState state, string action, string actor, string reason, object data)
    {
        db.Execute("UPDATE AfterSaleCases SET State=$s,Version=Version+1,UpdatedAt=$now WHERE Id=$id", ("s", state.ToString()), ("now", Now()), ("id", c.Id));
        AfterHistory(db, c.Id, action, actor, reason, data); TouchOrder(db, c.OrderId, action, actor, reason);
        Event(db, "aftersale:" + c.Id, c.Version + 1, "AfterSaleChanged", new { caseId = c.Id, state, action });
    }
    private static bool HasActiveAfterSale(DbSession db, string orderId) => db.Scalar<int>("SELECT COUNT(*) FROM AfterSaleCases WHERE OrderId=$id AND State IN('Open','Approved')", ("id", orderId)) > 0;
    private static void GuardAfterSale(DbSession db, string orderId)
    { if (HasActiveAfterSale(db, orderId)) throw new ConflictException("该订单存在处理中售后，已拦截发货和直接取消。请先在售后中心处理。"); }
    private static void GuardShipmentAfterSale(DbSession db, string shipmentId)
    { foreach (var order in Allocations(db, shipmentId).Select(l => l.OrderId).Distinct()) GuardAfterSale(db, order); }
    private static long ClaimedShipmentUnits(DbSession db, string shipmentLineId) => db.Scalar<long>("""
        SELECT COALESCE(SUM(l.QuantityUnits),0) FROM AfterSaleLines l JOIN AfterSaleCases c ON c.Id=l.CaseId WHERE l.ShipmentLineId=$id AND c.State NOT IN('Rejected','Withdrawn')
        """, ("id", shipmentLineId));
    private static long ClaimedRefund(DbSession db, string orderId) => db.Scalar<long>("SELECT COALESCE(SUM(RefundCents),0) FROM AfterSaleCases WHERE OrderId=$id AND State NOT IN('Rejected','Withdrawn')", ("id", orderId));

    public AfterSaleResult CreateAfterSale(CreateAfterSaleRequest r) => AfterCommand(r.OperationId, "AfterSaleCreate", r, r.Actor, r.Reason, db =>
    {
        if (!Enum.IsDefined(r.Kind) || r.Lines is null || r.Lines.Count > 1000 || r.Lines.Any(l => l is null)) throw new BusinessRuleException("售后类型或明细不合法。");
        var sourceNo = AfterText(r.SourceCaseNumber, "来源售后编号", 100); var cents = RefundMoney(r.RefundAmount); var order = Get(db, r.OrderId);
        var basis = Hash(new { r.OrderId, sourceNo, r.Kind, cents, lines = r.Lines.OrderBy(l => l.ShipmentLineId, StringComparer.Ordinal).ToList() });
        var existing = db.Query("SELECT Id,RequestHash FROM AfterSaleCases WHERE TenantId=$t AND Platform=$p AND ShopId=$shop AND SourceCaseNumber=$n",
            x => (Id: x.GetString(0), Hash: x.GetString(1)), ("t", Tenant(db)), ("p", order.Source.Platform.ToString()), ("shop", order.Source.ShopId), ("n", sourceNo)).SingleOrDefault();
        if (existing.Id is not null)
        { if (existing.Hash != basis) throw new ConflictException("同一来源售后编号已用于不同申请。"); return AfterResult(db, existing.Id, "该来源售后已存在，没有重复申请。", true); }
        Version(order, r.ExpectedOrderVersion);
        if (order.State != ReviewState.Approved || order.Normalized is null || order.Source.PaymentState != PaymentState.Paid) throw new ConflictException("当前只为已付款、已审核订单创建售后。请先处理订单审核问题。");
        if (cents > Money(order.Source.PaidAmount) - ClaimedRefund(db, order.Id)) throw new ConflictException("申请退款金额超过原单剩余可申请金额；处理中申请也占用额度。");
        if (r.Kind == AfterSaleKind.CancelUnshipped)
        {
            if (r.Lines.Count != 0 || cents != Money(order.Source.PaidAmount) || ClaimedRefund(db, order.Id) != 0) throw new BusinessRuleException("未出库取消按整单处理，金额必须等于实付，不能另选出库行或叠加其他申请。");
            if (db.Scalar<int>("SELECT COUNT(*) FROM ShipmentLines l JOIN Shipments s ON s.Id=l.ShipmentId WHERE l.OrderId=$o AND s.State='Shipped'", ("o", order.Id)) > 0)
                throw new ConflictException("订单已有出库记录，不能按未出库整单取消；应选择已出库仅退款或退货退款。");
        }
        else if (r.Lines.Count == 0 || r.Lines.Select(l => l.ShipmentLineId).Distinct().Count() != r.Lines.Count) throw new BusinessRuleException("请选择不重复的已出库商品行和售后数量。");
        var id = Guid.NewGuid().ToString("N"); var number = "AS-" + DateTime.UtcNow.ToString("yyyyMMdd") + "-" + id[..10].ToUpperInvariant();
        db.Execute("""
            INSERT INTO AfterSaleCases(Id,Number,TenantId,OrderId,Platform,ShopId,SourceCaseNumber,RequestHash,Kind,State,RefundCents,CreatedAt,UpdatedAt)
            VALUES($id,$n,$t,$o,$p,$shop,$source,$hash,$kind,'Open',$money,$now,$now)
            """, ("id", id), ("n", number), ("t", Tenant(db)), ("o", order.Id), ("p", order.Source.Platform.ToString()), ("shop", order.Source.ShopId),
            ("source", sourceNo), ("hash", basis), ("kind", r.Kind.ToString()), ("money", cents), ("now", Now()));
        void Line(string? shipmentLineId, NormalizedLine snapshot, long units, long originalShipmentUnits)
        {
            var lineId = Guid.NewGuid().ToString("N");
            db.Execute("INSERT INTO AfterSaleLines VALUES($id,$case,$ship,$external,$q,$snapshot)", ("id", lineId), ("case", id), ("ship", shipmentLineId), ("external", snapshot.ExternalLineId), ("q", units), ("snapshot", Json(snapshot)));
            if (r.Kind != AfterSaleKind.ReturnRefund) return;
            var components = db.Query("SELECT SkuId,QuantityUnits FROM ShipmentComponents WHERE ShipmentLineId=$id", x => (Sku: x.GetString(0), Units: x.GetInt64(1)), ("id", shipmentLineId));
            foreach (var component in components)
            {
                var expected = Quantity(FromQuantity(component.Units) * units / originalShipmentUnits);
                if (expected == 0) throw new BusinessRuleException("退货组件数量必须精确到 0.001 且大于 0。");
                db.Execute("INSERT INTO ReturnComponents(CaseLineId,SkuId,ExpectedUnits) VALUES($line,$sku,$q)", ("line", lineId), ("sku", component.Sku), ("q", expected));
            }
        }
        if (r.Kind == AfterSaleKind.CancelUnshipped) foreach (var line in order.Normalized.Lines) Line(null, line, Quantity(line.OrderedQuantity), 0);
        else foreach (var item in r.Lines)
        {
            var source = db.Query("SELECT l.SnapshotJson,l.QuantityUnits FROM ShipmentLines l JOIN Shipments s ON s.Id=l.ShipmentId WHERE l.Id=$id AND l.OrderId=$order AND s.State='Shipped'",
                x => (Snapshot: x.GetString(0), Units: x.GetInt64(1)), ("id", item.ShipmentLineId), ("order", order.Id)).SingleOrDefault();
            if (source.Snapshot is null) throw new BusinessRuleException("售后行不属于该订单的已出库记录。");
            var units = Quantity(item.Quantity);
            if (units <= 0 || units > source.Units - ClaimedShipmentUnits(db, item.ShipmentLineId)) throw new ConflictException("售后数量超过此出库行的剩余可申请量，或数量为零。");
            Line(item.ShipmentLineId, FromJson<NormalizedLine>(source.Snapshot), units, source.Units);
        }
        AfterHistory(db, id, "Apply", r.Actor, r.Reason, new { r.Kind, r.RefundAmount, sourceNo }); TouchOrder(db, order.Id, "AfterSaleApply", r.Actor, number);
        Event(db, "aftersale:" + id, 1, "AfterSaleOpened", new { caseId = id, orderId = order.Id });
        return AfterResult(db, id, "售后申请已保存，相关订单立即拦截发货。尚未退款，也未增加退货库存。");
    });

    public AfterSaleResult ApproveAfterSale(string id, AfterSaleActionRequest r) => AfterCommand(r.OperationId, "AfterSaleApprove", new { id, r }, r.Actor, r.Reason, db =>
    {
        var c = AfterCase(db, id); AfterVersion(c, r.ExpectedVersion); if (c.State != AfterSaleState.Open) throw new ConflictException("只有待审核售后可以批准。");
        if (c.Kind == AfterSaleKind.CancelUnshipped)
        {
            var packages = db.Query("SELECT DISTINCT s.Id,s.State FROM Shipments s JOIN ShipmentLines l ON l.ShipmentId=s.Id WHERE l.OrderId=$o AND s.State NOT IN('Voided','Superseded')",
                x => (Id: x.GetString(0), State: Enum.Parse<ShipmentState>(x.GetString(1))), ("o", c.OrderId));
            if (packages.Any(p => p.State == ShipmentState.Shipped)) throw new ConflictException("已出库，不能按未出库取消。");
            if (packages.Any(p => p.State is ShipmentState.Picking or ShipmentState.Packed) && !r.ConfirmPickedGoodsReturned) throw new ConflictException("已拣货或封箱，须先确认整个相关包裹的实物已归还，包括合包中的其他订单商品。");
            foreach (var p in packages)
            {
                var package = Shipment(db, p.Id); Transition(db, package, ShipmentState.Voided, "AfterSaleVoid", r.Actor, r.Reason, new { caseId = id, r.ConfirmPickedGoodsReturned });
                foreach (var orderId in Allocations(db, p.Id).Select(l => l.OrderId).Distinct()) TouchOrder(db, orderId, "AfterSaleVoid", r.Actor, r.Reason);
            }
            var reservations = db.Query("SELECT SkuId,QuantityUnits-ConsumedUnits FROM Reservations WHERE OrderId=$o AND State='Active' ORDER BY SkuId", x => (Sku: x.GetString(0), Units: x.GetInt64(1)), ("o", c.OrderId));
            foreach (var item in reservations)
            {
                if (db.Execute("UPDATE InventoryBalances SET ReservedUnits=ReservedUnits-$q,Version=Version+1 WHERE TenantId=$t AND OwnerId=$owner AND WarehouseId=$w AND SkuId=$sku AND ReservedUnits>=$q",
                    ("q", item.Units), ("t", Tenant(db)), ("owner", OwnerOf(db)), ("w", WarehouseOf(db)), ("sku", item.Sku)) != 1) throw new ConflictException("预占余额异常，取消未提交。");
                db.Execute("UPDATE Reservations SET State='Released' WHERE OrderId=$o AND SkuId=$sku", ("o", c.OrderId), ("sku", item.Sku));
                Ledger(db, item.Sku, r.OperationId, "AfterSaleRelease", c.OrderId, 0, -item.Units, r.Reason);
                db.Execute("UPDATE InventoryLedger SET AfterSaleId=$case WHERE OperationId=$op AND Action='AfterSaleRelease' AND SkuId=$sku", ("case", id), ("op", r.OperationId), ("sku", item.Sku));
                _fault?.Invoke("aftersale-after-stock");
            }
            var order = Get(db, c.OrderId); SetState(db, order, ReviewState.Cancelled, order.Normalized, [], "AfterSaleCancel", r.Actor + "：" + r.Reason);
        }
        AfterTransition(db, c, AfterSaleState.Approved, "Approve", r.Actor, r.Reason, new { r.ConfirmPickedGoodsReturned });
        return AfterResult(db, id, c.Kind == AfterSaleKind.CancelUnshipped ? "已作废相关未出库包裹并取消原订单，预占已释放；退款状态仍需单独跟踪。合包中的其他订单可重新配单。" : "售后已批准。退货实物与退款记录分别处理。");
    });
    public AfterSaleResult RejectAfterSale(string id, AfterSaleActionRequest r) => EndAfterSale(id, r, false);
    public AfterSaleResult WithdrawAfterSale(string id, AfterSaleActionRequest r) => EndAfterSale(id, r, true);
    private AfterSaleResult EndAfterSale(string id, AfterSaleActionRequest r, bool withdraw) => AfterCommand(r.OperationId, withdraw ? "AfterSaleWithdraw" : "AfterSaleReject", new { id, r }, r.Actor, r.Reason, db =>
    {
        var c = AfterCase(db, id); AfterVersion(c, r.ExpectedVersion);
        if (c.State != AfterSaleState.Open && (!withdraw || c.State != AfterSaleState.Approved || c.Kind == AfterSaleKind.CancelUnshipped)) throw new ConflictException("当前售后状态不能驳回或撤回；已取消的原订单不会复活。");
        if (c.RefundState is RefundTrackingState.Unknown or RefundTrackingState.EvidenceRecorded || db.Scalar<int>("SELECT COUNT(*) FROM ReturnReceipts WHERE CaseId=$id", ("id", id)) > 0)
            throw new ConflictException("已有退货入账或退款结果已登记/不明，不能撤回释放额度。");
        if (db.Scalar<int>("SELECT COUNT(*) FROM BillRows r JOIN BillBatches b ON b.Id=r.BatchId WHERE b.State='Active' AND r.Kind='Refund' AND (r.CaseId=$id OR (r.OrderId=$o AND r.SourceCase=(SELECT SourceCaseNumber FROM AfterSaleCases WHERE Id=$id)))", ("id", id), ("o", c.OrderId)) > 0)
            throw new ConflictException("已有有效账单退款关联，请先处理账单差异，不能撤回或驳回释放额度。");
        AfterTransition(db, c, withdraw ? AfterSaleState.Withdrawn : AfterSaleState.Rejected, withdraw ? "Withdraw" : "Reject", r.Actor, r.Reason, new { });
        return AfterResult(db, id, "申请已结束，释放售后数量和金额额度；无其他活动售后时恢复发货。库存没有变化。");
    });
    public AfterSaleResult CloseAfterSale(string id, AfterSaleActionRequest r) => AfterCommand(r.OperationId, "AfterSaleClose", new { id, r }, r.Actor, r.Reason, db =>
    {
        var c = AfterCase(db, id); AfterVersion(c, r.ExpectedVersion);
        if (c.State != AfterSaleState.Approved || c.RefundState != RefundTrackingState.EvidenceRecorded) throw new ConflictException("须先批准售后并登记退款凭证，才能本地结案；凭证登记不等于平台核验。");
        var shortReturn = false;
        if (c.Kind == AfterSaleKind.ReturnRefund)
        {
            var amounts = db.Query("SELECT SUM(ExpectedUnits),SUM(GoodUnits+DamagedUnits) FROM ReturnComponents c JOIN AfterSaleLines l ON l.Id=c.CaseLineId WHERE l.CaseId=$id",
                x => (Expected: x.GetInt64(0), Received: x.GetInt64(1)), ("id", id)).Single();
            if (amounts.Received == 0) throw new ConflictException("尚未收到任何退货，不能按退货流程结案。");
            shortReturn = amounts.Received < amounts.Expected;
            if (shortReturn && !r.AcceptShortReturn) throw new ConflictException("实际收货不足；继续收货，或明确接受少退并填写差异处理原因。");
        }
        db.Execute("UPDATE AfterSaleCases SET ShortReturnAccepted=$v WHERE Id=$id", ("v", shortReturn ? 1 : 0), ("id", id));
        AfterTransition(db, c, AfterSaleState.Closed, "CloseLocal", r.Actor, r.Reason, new { shortReturn, source = "LocalEvidenceNotPlatformVerified" });
        return AfterResult(db, id, shortReturn ? "已按人工凭证本地结案，少退差异保留；未收到的数量没有入库。" : "已按人工凭证本地结案；平台退款尚未由系统核验。");
    });
}
