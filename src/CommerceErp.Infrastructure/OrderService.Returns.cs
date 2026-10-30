using CommerceErp.Core;
using static CommerceErp.Infrastructure.CommerceDatabase;
using static CommerceErp.Core.BusinessUnits;

namespace CommerceErp.Infrastructure;

public sealed partial class OrderService
{
    public AfterSaleResult InspectReturn(string id, InspectReturnRequest r) => AfterCommand(r.OperationId, "ReturnInspect", new { id, r }, r.Actor, r.Reason, db =>
    {
        var c = AfterCase(db, id); var receiptNumber = AfterText(r.ReceiptNumber, "退货收货批次号", 100); var tracking = AfterText(r.ReturnTracking, "退回运单号", 100, true);
        if (r.Lines is null || r.Lines.Count is < 1 or > 2000 || r.Lines.Any(l => l is null) || r.Lines.Select(l => (l.CaseLineId, l.SkuId)).Distinct().Count() != r.Lines.Count)
            throw new BusinessRuleException("质检须包含不重复的实物组件行。");
        var basis = Hash(new { id, receiptNumber, tracking, lines = r.Lines.OrderBy(l => l.CaseLineId, StringComparer.Ordinal).ThenBy(l => l.SkuId, StringComparer.Ordinal).ToList() });
        var existing = db.Query("SELECT CaseId,RequestHash FROM ReturnReceipts WHERE TenantId=$t AND ReceiptNumber=$n", x => (CaseId: x.GetString(0), Hash: x.GetString(1)), ("t", Tenant(db)), ("n", receiptNumber)).SingleOrDefault();
        if (existing.CaseId is not null)
        { if (existing.CaseId != id || existing.Hash != basis) throw new ConflictException("该退货收货批次号已有不同的记录。"); return AfterResult(db, id, "该收货批次已入账，没有再次增加库存。", true); }
        AfterVersion(c, r.ExpectedVersion);
        if (c.State != AfterSaleState.Approved || c.Kind != AfterSaleKind.ReturnRefund) throw new ConflictException("只有已批准的退货退款单可以质检入库。");
        var rows = r.Lines.Select(l => (l.CaseLineId, l.SkuId, Good: Quantity(l.GoodQuantity), Damaged: Quantity(l.DamagedQuantity))).ToList();
        foreach (var line in rows)
        {
            if (line.Good + line.Damaged == 0) throw new BusinessRuleException("每条收货行的良品与残次品合计须大于 0。");
            var updated = db.Execute("""
                UPDATE ReturnComponents SET GoodUnits=GoodUnits+$g,DamagedUnits=DamagedUnits+$bad
                WHERE CaseLineId=$line AND SkuId=$sku AND ExpectedUnits-GoodUnits-DamagedUnits >= $g+$bad
                AND EXISTS(SELECT 1 FROM AfterSaleLines l WHERE l.Id=CaseLineId AND l.CaseId=$case)
                """, ("g", line.Good), ("bad", line.Damaged), ("line", line.CaseLineId), ("sku", line.SkuId), ("case", id));
            if (updated != 1) throw new ConflictException("质检商品不属于该售后单，或数量超过尚未收货的数量；本批次未入账。");
        }
        var receiptId = Guid.NewGuid().ToString("N");
        db.Execute("INSERT INTO ReturnReceipts VALUES($id,$t,$case,$n,$tracking,$hash,$actor,$reason,$now)",
            ("id", receiptId), ("t", Tenant(db)), ("case", id), ("n", receiptNumber), ("tracking", tracking), ("hash", basis), ("actor", r.Actor), ("reason", r.Reason), ("now", Now()));
        foreach (var row in rows) db.Execute("INSERT INTO ReturnReceiptLines VALUES($receipt,$line,$sku,$good,$bad)",
            ("receipt", receiptId), ("line", row.CaseLineId), ("sku", row.SkuId), ("good", row.Good), ("bad", row.Damaged));
        foreach (var group in rows.GroupBy(l => l.SkuId).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var good = group.Sum(l => l.Good); var damaged = group.Sum(l => l.Damaged);
            if (good > 0)
            {
                if (db.Execute("UPDATE InventoryBalances SET OnHandUnits=OnHandUnits+$q,Version=Version+1 WHERE TenantId=$t AND OwnerId=$o AND WarehouseId=$w AND SkuId=$sku AND OnHandUnits <= 1000000000000-$q",
                    ("q", good), ("t", Tenant(db)), ("o", OwnerOf(db)), ("w", WarehouseOf(db)), ("sku", group.Key)) != 1) throw new ConflictException("良品库存记录不存在或超过上限，收货未提交。");
                Ledger(db, group.Key, r.OperationId, "ReturnGood", c.OrderId, good, 0, receiptNumber + "：" + r.Reason);
                db.Execute("UPDATE InventoryLedger SET AfterSaleId=$case,ReturnReceiptId=$receipt WHERE TenantId=$t AND OperationId=$op AND Action='ReturnGood' AND SkuId=$sku",
                    ("case", id), ("receipt", receiptId), ("t", Tenant(db)), ("op", r.OperationId), ("sku", group.Key));
            }
            if (damaged > 0)
            {
                if (db.Execute("UPDATE QuarantineBalances SET QuantityUnits=QuantityUnits+$q,Version=Version+1 WHERE TenantId=$t AND OwnerId=$o AND WarehouseId=$w AND SkuId=$sku AND QuantityUnits <= 1000000000000-$q",
                    ("q", damaged), ("t", Tenant(db)), ("o", OwnerOf(db)), ("w", WarehouseOf(db)), ("sku", group.Key)) != 1) throw new ConflictException("隔离库存记录不存在或超过上限，收货未提交。");
                db.Execute("""
                    INSERT INTO QuarantineLedger(TenantId,OwnerId,WarehouseId,SkuId,CaseId,ReceiptId,OperationId,QuantityUnits,BalanceAfter,Actor,Reason,CreatedAt)
                    SELECT TenantId,OwnerId,WarehouseId,SkuId,$case,$receipt,$op,$q,QuantityUnits,$actor,$reason,$now FROM QuarantineBalances
                    WHERE TenantId=$t AND OwnerId=$o AND WarehouseId=$w AND SkuId=$sku
                    """, ("case", id), ("receipt", receiptId), ("op", r.OperationId), ("q", damaged), ("actor", r.Actor), ("reason", r.Reason), ("now", Now()),
                    ("t", Tenant(db)), ("o", OwnerOf(db)), ("w", WarehouseOf(db)), ("sku", group.Key));
            }
            _fault?.Invoke("aftersale-after-stock");
        }
        AfterTransition(db, c, c.State, "InspectReturn", r.Actor, r.Reason, new { receiptId, receiptNumber, tracking, r.Lines });
        return AfterResult(db, id, "本批次质检已入账：良品增加主库存，残次品进入独立隔离库存；退款状态未改变。");
    });

    public AfterSaleResult RecordRefund(string id, RecordRefundRequest r) => AfterCommand(r.OperationId, "RefundRecord", new { id, r }, r.Actor, r.Reason, db =>
    {
        var c = AfterCase(db, id); var eventKey = AfterText(r.EventKey, "退款记录事件号", 100); var reference = AfterText(r.RefundReference, "退款凭证号", 100, r.Status != RefundTrackingState.EvidenceRecorded);
        var evidence = AfterText(r.Evidence, "凭证或回查说明", 1000); var cents = RefundMoney(r.Amount);
        if (!Enum.IsDefined(r.Status) || r.Status == RefundTrackingState.Pending || r.ObservedAt == default || r.ObservedAt > DateTimeOffset.UtcNow.AddMinutes(5)) throw new BusinessRuleException("退款跟踪状态或观察时间不合法。");
        var basis = Hash(new { id, eventKey, r.Status, cents, reference, evidence, observedAt = r.ObservedAt.ToUniversalTime(), r.ConfirmLocalEvidence });
        var existing = db.Query("SELECT CaseId,RequestHash FROM RefundObservations WHERE TenantId=$t AND EventKey=$key", x => (Case: x.GetString(0), Hash: x.GetString(1)), ("t", Tenant(db)), ("key", eventKey)).SingleOrDefault();
        if (existing.Case is not null)
        { if (existing.Case != id || existing.Hash != basis) throw new ConflictException("该退款事件号已有不同记录。"); return AfterResult(db, id, "该退款事件已记录，没有再次记账。", true); }
        AfterVersion(c, r.ExpectedVersion);
        if (c.State != AfterSaleState.Approved || c.RefundState == RefundTrackingState.EvidenceRecorded) throw new ConflictException("当前售后状态不允许新增退款跟踪记录，已登记凭证不能降级覆盖。");
        if (cents != c.RefundCents) throw new BusinessRuleException("记录金额必须与本售后单申请金额一致；不能在跟踪时改变退款金额。");
        var lastObserved = db.Scalar<string>("SELECT ObservedAt FROM RefundObservations WHERE CaseId=$id ORDER BY ObservedAt DESC" + db.Limit(1), ("id", id));
        if (lastObserved is not null && r.ObservedAt.ToUniversalTime() < DateTimeOffset.Parse(lastObserved)) throw new ConflictException("旧的退款观察记录不能覆盖较新状态。");
        if (r.Status == RefundTrackingState.EvidenceRecorded)
        {
            if (!r.ConfirmLocalEvidence) throw new BusinessRuleException("请确认本次是人工凭证登记，未经系统平台核验，也不会发起真实退款。");
            if (db.Scalar<int>("SELECT COUNT(*) FROM RefundEvidence WHERE TenantId=$t AND Platform=$p AND ShopId=$shop AND RefundReference=$ref", ("t", Tenant(db)), ("p", c.Platform), ("shop", c.ShopId), ("ref", reference)) > 0)
                throw new ConflictException("该店铺的退款凭证号已用于另一售后单，不能重复登记。");
        }
        var observationId = db.InsertIdentity("""
            INSERT INTO RefundObservations(TenantId,CaseId,EventKey,RequestHash,Status,AmountCents,RefundReference,Evidence,ObservedAt,Actor,Reason,CreatedAt)
            VALUES($t,$case,$key,$hash,$state,$money,$ref,$evidence,$observed,$actor,$reason,$now)
            """, ("t", Tenant(db)), ("case", id), ("key", eventKey), ("hash", basis), ("state", r.Status.ToString()), ("money", cents), ("ref", reference), ("evidence", evidence),
            ("observed", r.ObservedAt.ToUniversalTime().ToString("O")), ("actor", r.Actor), ("reason", r.Reason), ("now", Now()));
        if (r.Status == RefundTrackingState.EvidenceRecorded) db.Execute("INSERT INTO RefundEvidence VALUES($case,$t,$p,$shop,$ref,$money,$observation)",
            ("case", id), ("t", Tenant(db)), ("p", c.Platform), ("shop", c.ShopId), ("ref", reference), ("money", cents), ("observation", observationId));
        db.Execute("UPDATE AfterSaleCases SET RefundState=$state,RefundReference=$ref WHERE Id=$id", ("state", r.Status.ToString()), ("ref", reference), ("id", id));
        AfterTransition(db, c, c.State, "RecordRefund", r.Actor, r.Reason, new { eventKey, r.Status, r.Amount, reference, source = "ManualNotPlatformVerified" });
        _fault?.Invoke("aftersale-after-refund");
        return AfterResult(db, id, r.Status == RefundTrackingState.EvidenceRecorded ? "人工退款凭证已登记；未调用退款接口，未经系统平台核验。库存没有变化。" : "退款跟踪状态已保存。结果不明时保留金额额度，不增加库存。");
    });
}
