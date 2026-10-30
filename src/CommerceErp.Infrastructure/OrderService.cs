using System.Security.Cryptography;
using System.Text;
using CommerceErp.Core;
using static CommerceErp.Infrastructure.CommerceDatabase;
using static CommerceErp.Core.BusinessUnits;

namespace CommerceErp.Infrastructure;

public sealed partial class OrderService
{
    private readonly CommerceDatabase _database;
    private readonly Action<string>? _fault;
    public OrderService(CommerceDatabase database, Action<string>? fault = null) { _database = database; _fault = fault; }
    private sealed record StoredOrder(string Id, ReviewState State, long Version, string SourceHash,
        SampleOrder Source, NormalizedOrder? Normalized, List<ValidationIssue> Issues, DateTimeOffset CreatedAt);
    private static readonly HashSet<string> InvalidEnvelopeCodes =
    ["SCHEMA_VERSION", "SYNTHETIC_ONLY", "INVALID_PLATFORM", "INVALID_STATE", "INVALID_ID", "INVALID_TIME", "CURRENCY_NOT_SUPPORTED", "INVALID_MONEY", "INVALID_LINES", "INVALID_LINE", "DUPLICATE_LINE", "INVALID_QUANTITY"];
    private static string Hash(object value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Json(value))));
    private static void OperationId(string value)
    { if (!Guid.TryParse(value, out var id) || id == Guid.Empty) throw new BusinessRuleException("OperationId 必须是非空 GUID；重试时保留原值。"); }
    private static void Reason(string? reason)
    { if (string.IsNullOrWhiteSpace(reason) || reason.Length > 500) throw new BusinessRuleException("请填写 1–500 字的操作原因。"); }
    private static void Version(StoredOrder row, long expected)
    { if (row.Version != expected) throw new ConflictException("订单已被其他操作修改，请刷新详情后重试。"); }
    private static StoredOrder Get(DbSession db, string id) => db.Query("""
        SELECT Id,State,Version,SourceHash,SourceJson,NormalizedJson,IssuesJson,CreatedAt
        FROM SalesOrders WHERE TenantId=$t AND Id=$id
        """, r => new StoredOrder(r.GetString(0), Enum.Parse<ReviewState>(r.GetString(1)), r.GetInt64(2), r.GetString(3),
            FromJson<SampleOrder>(r.GetString(4)), r.IsDBNull(5) ? null : FromJson<NormalizedOrder>(r.GetString(5)),
            FromJson<List<ValidationIssue>>(r.GetString(6)), DateTimeOffset.Parse(r.GetString(7))), ("t", Tenant(db)), ("id", id))
        .SingleOrDefault() ?? throw new BusinessRuleException("订单不存在。");
    private static ValidationReport ValidateEnvelope(DbSession db, SampleOrder? source)
    {
        if (source is null) throw new BusinessRuleException("订单不能为空。"); RequireTenant(db,source.TenantId);
        if(BusinessMode(db)&&source.DataKind!="manual")throw new BusinessRuleException("正式工作区仅接受明确标记的人工订单，平台连接器尚未启用。");
        var report = new OrderNormalizer().Validate(source, ReadCatalog(db),BusinessMode(db));
        var malformed = report.Issues.FirstOrDefault(i => InvalidEnvelopeCodes.Contains(i.Code));
        if (malformed is not null) throw new BusinessRuleException($"{malformed.Code}: {malformed.Message}");
        if (db.Scalar<long>("SELECT COUNT(*) FROM Shops WHERE TenantId=$t AND Platform=$p AND Id=$s AND EXISTS(SELECT 1 FROM MasterShopData x WHERE x.ShopId=Shops.Id AND x.Enabled=1)", ("t", source.TenantId), ("p", source.Platform.ToString()), ("s", source.ShopId)) != 1)
            throw new BusinessRuleException("店铺未登记或已停用。");
        return report;
    }
    private static T? Replay<T>(DbSession db, string operationId, string kind, string hash) where T : class
    {
        var record = db.Query("SELECT Kind,RequestHash,ResponseJson FROM Operations WHERE TenantId=$t AND OperationId=$op",
            r => (Kind: r.GetString(0), Hash: r.GetString(1), Response: r.GetString(2)), ("t", Tenant(db)), ("op", operationId)).SingleOrDefault();
        if (record.Response is null) return null;
        if (record.Kind != kind || record.Hash != hash) throw new ConflictException("该操作标识已被不同请求使用，请勿复用幂等键提交其他业务。");
        return FromJson<T>(record.Response);
    }
    private static void SaveOperation(DbSession db, string operationId, string kind, string hash, object result) =>
        db.Execute("INSERT INTO Operations VALUES($t,$op,$kind,$hash,$json,$now)", ("t", Tenant(db)), ("op", operationId),
            ("kind", kind), ("hash", hash), ("json", Json(result)), ("now", Now()));
    private OrderCommandResult OrderCommand(string operationId, string kind, object request, Func<DbSession, OrderCommandResult> action)
    {
        OperationId(operationId); var hash = Hash(request);
        return _database.Write(db =>
        {
            var replay = Replay<OrderCommandResult>(db, operationId, kind, hash);
            if (replay is not null) return replay with { Replayed = true, Message = "相同操作已处理，返回原操作结果；请刷新查看订单最新状态。" };
            var result = action(db); SaveOperation(db, operationId, kind, hash, result);
            _fault?.Invoke("before-commit"); return result;
        });
    }
    private static OrderCommandResult Result(DbSession db, string id, string message, bool replayed = false)
    { var row = Get(db, id); return new(id, row.State, row.Version, replayed, message); }
    private static void History(DbSession db, string id, string action, string from, string to, string reason) =>
        db.Execute("INSERT INTO OrderHistory(OrderId,Action,FromState,ToState,Reason,RuleVersion,CreatedAt) VALUES($id,$action,$from,$to,$reason,$rule,$now)",
            ("id", id), ("action", action), ("from", from), ("to", to), ("reason", reason), ("rule", RuleVersion), ("now", Now()));
    private static void Snapshot(DbSession db, string id) => db.Execute("""
        INSERT INTO OrderSnapshots SELECT Id,Version,State,SourceJson,NormalizedJson,IssuesJson,$now FROM SalesOrders WHERE Id=$id
        """, ("id", id), ("now", Now()));
    private static void Event(DbSession db, string aggregateId, long version, string type, object payload) =>
        db.Execute("INSERT INTO Outbox(Id,TenantId,AggregateId,AggregateVersion,EventType,PayloadJson,CreatedAt,UpdatedAt,NextAttemptAt) VALUES($id,$t,$a,$v,$type,$payload,$now,$now,$now)",
            ("id", Guid.NewGuid().ToString("N")), ("t", Tenant(db)), ("a", aggregateId), ("v", version), ("type", type), ("payload", Json(payload)), ("now", Now()));
    private static void Lines(DbSession db, string id, NormalizedOrder? normalized)
    {
        db.Execute("DELETE FROM OrderLines WHERE OrderId=$id", ("id", id));
        IndexOrder(db,id,Get(db,id).Source);
        if (normalized is null) return;
        foreach (var line in normalized.Lines)
            db.Execute("INSERT INTO OrderLines VALUES($id,$line,$sku,$q,$v,$json)", ("id", id), ("line", line.ExternalLineId),
                ("sku", line.InternalSkuId), ("q", Quantity(line.OrderedQuantity)), ("v", line.MappingVersion), ("json", Json(line)));
    }
    private static void SetState(DbSession db, StoredOrder row, ReviewState state, NormalizedOrder? normalized,
        List<ValidationIssue> issues, string action, string reason)
    {
        var version = row.Version + 1;
        db.Execute("UPDATE SalesOrders SET State=$state,Version=$v,NormalizedJson=$n,IssuesJson=$i,UpdatedAt=$now WHERE Id=$id AND TenantId=$t",
            ("state", state.ToString()), ("v", version), ("n", normalized is null ? null : Json(normalized)), ("i", Json(issues)), ("now", Now()), ("id", row.Id), ("t", Tenant(db)));
        Lines(db, row.Id, normalized); History(db, row.Id, action, row.State.ToString(), state.ToString(), reason); Snapshot(db, row.Id);
        Event(db, row.Id, version, "OrderStateChanged", new { orderId = row.Id, state, version, ruleVersion = RuleVersion });
    }

    public OrderCommandResult Import(ImportOrderRequest request) => OrderCommand(request.OperationId, "Import", request, db =>
    {
        var validation = ValidateEnvelope(db, request.Order); var source = request.Order; var hash = Hash(source);
        var existing = db.Scalar<string>("SELECT Id FROM SalesOrders WHERE TenantId=$t AND Platform=$p AND ShopId=$s AND ExternalOrderId=$e",
            ("t", source.TenantId), ("p", source.Platform.ToString()), ("s", source.ShopId), ("e", source.ExternalOrderId));
        if (existing is not null)
        {
            if (Get(db, existing).SourceHash != hash) throw new ConflictException("同一外部订单已存在但内容不同。本版本禁止通知覆盖；请对未审核订单使用有版本控制的更正功能。");
            return Result(db, existing, "订单已存在，未重复建单或预占。", true);
        }
        var id = Guid.NewGuid().ToString("N"); var state = validation.Passed ? ReviewState.PendingReview : ReviewState.Exception;
        var now = Now();
        db.Execute("""
            INSERT INTO SalesOrders VALUES($id,$t,$p,$shop,$external,$o,$w,$state,1,$hash,$source,$normalized,$issues,$paid,$count,$now,$now)
            """, ("id", id), ("t", source.TenantId), ("p", source.Platform.ToString()), ("shop", source.ShopId), ("external", source.ExternalOrderId),
            ("o", OwnerOf(db)), ("w", WarehouseOf(db)), ("state", state.ToString()), ("hash", hash), ("source", Json(source)),
            ("normalized", validation.Order is null ? null : Json(validation.Order)), ("issues", Json(validation.Issues)), ("paid", Money(source.PaidAmount)), ("count", source.Lines.Count), ("now", now));
        Lines(db, id, validation.Order); History(db, id, "Import", "", state.ToString(), "模拟订单接入"); Snapshot(db, id);
        Event(db, id, 1, "OrderAccepted", new { orderId = id, source.Platform, source.ShopId, source.ExternalOrderId });
        if (request.AutoReview) ReviewCore(db, Get(db, id), request.OperationId);
        return Result(db, id, "订单已入库，审单结果与库存变动已在同一事务中保存。");
    });

    public OrderCommandResult Review(string id, OrderActionRequest request) => OrderCommand(request.OperationId, "Review", new { id, request }, db =>
    {
        var row = Get(db, id); Version(row, request.ExpectedVersion);
        ReviewCore(db, row, request.OperationId); return Result(db, id, "审单完成；已审核订单不会重复预占。");
    });
    private void ReviewCore(DbSession db, StoredOrder row, string operationId)
    {
        if (row.State == ReviewState.Cancelled) throw new ConflictException("订单已取消，不能重新审核或恢复预占。");
        if (row.State == ReviewState.Approved) return;
        var report = ValidateEnvelope(db, row.Source); var issues = report.Issues.ToList(); var normalized = report.Order;
        if (row.Source.FulfillmentState == FulfillmentState.Cancelled)
        { SetState(db, row, ReviewState.Cancelled, normalized, issues, "Review", "来源订单已取消，未预占库存"); return; }
        if (issues.Any(i => i.Severity == "Error"))
        { SetState(db, row, ReviewState.Exception, normalized, issues, "Review", "模型或 SKU 映射校验失败"); return; }
        if (row.Source.PaymentState != PaymentState.Paid || row.Source.FulfillmentState != FulfillmentState.Unfulfilled)
        { SetState(db, row, ReviewState.OnHold, normalized, issues, "Review", "付款或履约状态不允许预占"); return; }
        var requirements = normalized!.StockRequirements.OrderBy(r => r.InternalSkuId, StringComparer.Ordinal).ToList();
        try { foreach (var item in requirements) _ = Quantity(item.Quantity); }
        catch (BusinessRuleException e)
        { issues.Add(new("Error", "STOCK_QUANTITY_LIMIT", "stock", e.Message)); SetState(db, row, ReviewState.Exception, normalized, issues, "Review", e.Message); return; }
        db.Savepoint("save","reserve_all");
        for (var i = 0; i < requirements.Count; i++)
        {
            var item = requirements[i]; var units = Quantity(item.Quantity);
            var affected = db.Execute("""
                UPDATE InventoryBalances SET ReservedUnits=ReservedUnits+$q,Version=Version+1
                WHERE TenantId=$t AND OwnerId=$o AND WarehouseId=$w AND SkuId=$s
                AND OnHandUnits-FrozenUnits-ReservedUnits-SafetyUnits >= $q
                """, ("q", units), ("t", Tenant(db)), ("o", OwnerOf(db)), ("w", WarehouseOf(db)), ("s", item.InternalSkuId));
            if (affected != 1)
            {
                db.Savepoint("rollback","reserve_all"); db.Savepoint("release","reserve_all");
                issues.Add(new("Error", "INSUFFICIENT_STOCK", item.InternalSkuId, $"{item.SkuCode} 需 {item.Quantity:0.###}，可承诺量不足；整单未预占。"));
                SetState(db, row, ReviewState.Exception, normalized, issues, "Review", "库存不足，整单预占已回滚"); return;
            }
            db.Execute("INSERT INTO Reservations(TenantId,OrderId,OwnerId,WarehouseId,SkuId,QuantityUnits,State) VALUES($t,$id,$o,$w,$s,$q,'Active')", ("t", Tenant(db)), ("id", row.Id), ("o", OwnerOf(db)), ("w", WarehouseOf(db)), ("s", item.InternalSkuId), ("q", units));
            Ledger(db, item.InternalSkuId, operationId, "Reserve", row.Id, 0, units, "自动审单预占");
            if (i == 0) _fault?.Invoke("after-first-reservation");
        }
        db.Savepoint("release","reserve_all"); _fault?.Invoke("after-reservations");
        SetState(db, row, ReviewState.Approved, normalized, issues, "Review", "规则通过，全部组件预占成功");
    }
    public OrderCommandResult Cancel(string id, OrderActionRequest request) => OrderCommand(request.OperationId, "Cancel", new { id, request }, db =>
    {
        Reason(request.Reason); var row = Get(db, id); Version(row, request.ExpectedVersion);
        if (row.State == ReviewState.Cancelled) return Result(db, id, "订单已取消，没有重复释放。", true);
        GuardAfterSale(db, id);
        if (db.Scalar<int>("SELECT COUNT(*) FROM ShipmentLines l JOIN Shipments s ON s.Id=l.ShipmentId WHERE l.OrderId=$id AND s.State NOT IN('Voided','Superseded')", ("id", id)) > 0)
            throw new ConflictException("订单存在有效出库单。未出库单须先作废；已部分或全部出库的订单须走售后流程，不能直接取消释放。");
        var active = db.Query("SELECT SkuId,QuantityUnits FROM Reservations WHERE TenantId=$t AND OrderId=$id AND State='Active' ORDER BY SkuId", r => (Sku: r.GetString(0), Quantity: r.GetInt64(1)), ("t", Tenant(db)), ("id", id));
        foreach (var item in active)
        {
            var affected = db.Execute("UPDATE InventoryBalances SET ReservedUnits=ReservedUnits-$q,Version=Version+1 WHERE TenantId=$t AND OwnerId=$o AND WarehouseId=$w AND SkuId=$s AND ReservedUnits >= $q",
                ("q", item.Quantity), ("t", Tenant(db)), ("o", OwnerOf(db)), ("w", WarehouseOf(db)), ("s", item.Sku));
            if (affected != 1) throw new InvalidDataException("库存预占与订单不一致，取消事务已回滚。");
            db.Execute("UPDATE Reservations SET State='Released' WHERE TenantId=$t AND OrderId=$id AND SkuId=$s AND State='Active'", ("t", Tenant(db)), ("id", id), ("s", item.Sku));
            Ledger(db, item.Sku, request.OperationId, "Release", id, 0, -item.Quantity, request.Reason);
            _fault?.Invoke("during-release");
        }
        SetState(db, row, ReviewState.Cancelled, row.Normalized, [], "Cancel", request.Reason);
        return Result(db, id, "订单已取消，相关预占已释放，实物在库数量保持不变。");
    });
    public OrderCommandResult Correct(string id, CorrectOrderRequest request) => OrderCommand(request.OperationId, "Correct", new { id, request }, db =>
    {
        Reason(request.Reason); var row = Get(db, id); Version(row, request.ExpectedVersion);
        if (row.State is ReviewState.Approved or ReviewState.Cancelled) throw new ConflictException("已审核或已取消订单不能更正来源内容。");
        var report = ValidateEnvelope(db, request.Order); var source = request.Order;
        if (source.TenantId != row.Source.TenantId || source.Platform != row.Source.Platform || source.ShopId != row.Source.ShopId || source.ExternalOrderId != row.Source.ExternalOrderId)
            throw new BusinessRuleException("更正不能修改订单所属租户、平台、店铺或外部订单号。");
        db.Execute("UPDATE SalesOrders SET SourceHash=$hash,SourceJson=$source,PaidCents=$paid,LineCount=$count WHERE Id=$id AND TenantId=$t",
            ("hash", Hash(source)), ("source", Json(source)), ("paid", Money(source.PaidAmount)), ("count", source.Lines.Count), ("id", id), ("t", Tenant(db)));
        SetState(db, row, report.Passed ? ReviewState.PendingReview : ReviewState.Exception, report.Order, report.Issues, "Correct", request.Reason);
        return Result(db, id, "更正已保存，保留历史快照；请重新审核后预占库存。");
    });
}
