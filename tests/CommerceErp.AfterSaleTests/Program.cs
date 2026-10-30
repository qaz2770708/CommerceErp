using System.Diagnostics;
using System.Reflection;
using CommerceErp.Core;
using CommerceErp.Infrastructure;
using static CommerceErp.Infrastructure.CommerceDatabase;

string Op() => Guid.NewGuid().ToString("N");
SampleOrder One(decimal quantity = 2) => SeedData.BaseOrder(Platform.Douyin) with
{ ExternalOrderId = Op(), ShippingFee = 0, OrderDiscount = 0, PaidAmount = 10, Lines = [SeedData.BaseOrder(Platform.Douyin).Lines[0] with { Quantity = quantity, GoodsAmount = 10, MerchantDiscount = 0 }] };
AfterSaleActionRequest Action(OrderService s, string id, bool returned = false, bool shortReturn = false) => new(Op(), s.AfterSaleDetails(id).Summary.Version, "售后测试员", "测试处理", returned, shortReturn);
InspectReturnRequest Receipt(OrderService s, string id, decimal good = 1, decimal bad = 0, string? number = null)
{ var d = s.AfterSaleDetails(id); var c = d.Components.First(); return new(Op(), d.Summary.Version, number ?? Op(), "TEST-RETURN", [new(c.CaseLineId, c.SkuId, good, bad)], "售后测试员", "实际质检"); }
if (args.Length == 3 && args[0] == "--crash-return")
{
    var s = new OrderService(new CommerceDatabase(args[1]), p => { if (p == "aftersale-after-stock") Environment.Exit(74); });
    s.InspectReturn(args[2], Receipt(s, args[2])); return 2;
}
var root = Path.Combine(Path.GetTempPath(), "CommerceErp.AfterSaleTests", Op()); Directory.CreateDirectory(root);
var results = new List<object>(); var failed = 0;
void Assert(bool condition, string message = "断言失败") { if (!condition) throw new InvalidOperationException(message); }
void Throws<T>(Action a) where T : Exception { try { a(); } catch (T) { return; } throw new InvalidOperationException("预期 " + typeof(T).Name); }
async Task Test(string name, Func<Task> run)
{ try { await run(); results.Add(new { name, passed = true }); Console.WriteLine("PASS " + name); } catch (Exception e) { failed++; results.Add(new { name, passed = false, error = e.ToString() }); Console.WriteLine("FAIL " + name + " / " + e); } }
Task Sync(Action action) { action(); return Task.CompletedTask; }
(CommerceDatabase Db, OrderService S) Fresh() { var db = new CommerceDatabase(Path.Combine(root, Op())); return (db, new(db)); }
string Order(OrderService s, SampleOrder? source = null)
{ var o = s.Import(new(Op(), source ?? One())); s.SaveShippingProfile(o.OrderId, new(Op(), o.Version, new("模拟用户", "00000000000", "测试省", "测试市", "测试区", "测试路"), true, "测试员", "测试地址")); return o.OrderId; }
string Draft(OrderService s, params string[] orders) => s.CreateShipment(new(Op(), orders.SelectMany(o => { var p = s.PrepareShipping(o); return p.Lines.Where(l => l.Remaining > 0).Select(l => new AllocationLine(o, l.ExternalLineId, l.Remaining, p.Order.Version)); }).ToList(), "测试员", "配单")).Id;
WarehouseActionRequest Wh(OrderService s, string id) => new(Op(), s.ShipmentDetails(id).Summary.Version, "测试员", "仓储操作");
void Pack(OrderService s, string id)
{ s.StartPicking(id, Wh(s, id)); foreach (var c in s.ShipmentDetails(id).Checks) s.ScanShipment(id, new(Op(), s.ShipmentDetails(id).Summary.Version, c.SkuCode, c.Required, "测试员")); s.PackShipment(id, Wh(s, id)); s.SaveWaybill(id, new(Op(), s.ShipmentDetails(id).Summary.Version, "TEST", "TEST-" + Op(), "测试员", "测试运单")); }
string Ship(OrderService s, string o, decimal qty = 0)
{ var p = s.PrepareShipping(o); var id = qty > 0 ? s.CreateShipment(new(Op(), [new(o, p.Lines[0].ExternalLineId, qty, p.Order.Version)], "测试员", "部分发货")).Id : Draft(s, o); Pack(s, id); s.DispatchShipment(id, Wh(s, id)); return id; }
CreateAfterSaleRequest Request(OrderService s, string o, AfterSaleKind kind = AfterSaleKind.ReturnRefund, decimal amount = 10, decimal qty = 0)
{ var p = s.PrepareAfterSale(o); return new(Op(), o, p.Order.Version, "LOCAL-" + Op(), kind, amount, kind == AfterSaleKind.CancelUnshipped ? [] : p.Lines.Where(l => l.Remaining > 0).Select(l => new AfterSaleSelection(l.ShipmentLineId, qty > 0 ? qty : l.Remaining)).ToList(), "售后测试员", "申请售后"); }
string Apply(OrderService s, string o, AfterSaleKind kind = AfterSaleKind.ReturnRefund, decimal amount = 10, decimal qty = 0) => s.CreateAfterSale(Request(s, o, kind, amount, qty)).Id;
RecordRefundRequest Refund(OrderService s, string id, RefundTrackingState state = RefundTrackingState.EvidenceRecorded, string? reference = null)
{ var c = s.AfterSaleDetails(id).Summary; return new(Op(), c.Version, Op(), state, c.RefundAmount, reference ?? "RF-" + Op(), "模拟人工凭证，仅用于测试", DateTimeOffset.UtcNow, true, "售后测试员", "登记观察结果"); }
InventoryRow Shirt(OrderService s) => s.Inventory().Single(i => i.SkuId == "sku-shirt");
void Healthy(OrderService s) { var a = s.CheckConsistency(); Assert(a.Passed, string.Join("；", a.Problems)); var b = s.AuditAfterSales(); Assert(b.Passed, string.Join("；", b.Problems)); }

await Test("Stage3 v2 数据迁移后出库、回传、库存和订单不重置", () => Sync(() =>
{
    var dir = Path.Combine(root, Op()); Directory.CreateDirectory(dir); File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures/stage3-v2.db"), Path.Combine(dir, "commerce.db"));
    var db = new CommerceDatabase(dir); var s = new OrderService(db); Assert(db.Read(d => d.Scalar<int>("SELECT MAX(Version) FROM SchemaMigrations")) == 10);
    Assert(s.List().Total == 2 && s.Shipments().Total == 4 && s.Deliveries().Count == 1 && Shirt(s).OnHand == 109.125m && s.Quarantine().All(q => q.Quantity == 0)); Healthy(s);
}));
await Test("售后申请立即拦截配单和直接取消，驳回后恢复", () => Sync(() =>
{
    var (_, s) = Fresh(); var o = Order(s); var id = Apply(s, o, AfterSaleKind.CancelUnshipped);
    Assert(s.Details(o).Summary.ActiveAfterSales == 1 && s.ShippingCandidates().Count == 0 && Shirt(s).Reserved == 2);
    Throws<ConflictException>(() => Draft(s, o)); Throws<ConflictException>(() => s.Cancel(o, new(Op(), s.Details(o).Summary.Version, "绕过售后")));
    s.RejectAfterSale(id, Action(s, id)); Assert(s.Details(o).Summary.ActiveAfterSales == 0); Draft(s, o); Healthy(s);
}));
await Test("未出库批准取消释放一次预占，退款状态独立", () => Sync(() =>
{
    var (_, s) = Fresh(); var o = Order(s); var pack = Draft(s, o); var id = Apply(s, o, AfterSaleKind.CancelUnshipped); var approve = Action(s, id);
    s.ApproveAfterSale(id, approve); Assert(s.ApproveAfterSale(id, approve).Replayed);
    Assert(s.Details(o).Summary.State == ReviewState.Cancelled && Shirt(s).Reserved == 0 && Shirt(s).OnHand == 100 && s.ShipmentDetails(pack).Summary.State == ShipmentState.Voided);
    Assert(s.AfterSaleDetails(id).Summary.RefundState == RefundTrackingState.Pending); Throws<ConflictException>(() => s.WithdrawAfterSale(id, Action(s, id)));
    s.RecordRefund(id, Refund(s, id)); s.CloseAfterSale(id, Action(s, id)); Healthy(s);
}));
await Test("拣货合包取消须确认整包归还，另一原单预占保留可重配", () => Sync(() =>
{
    var (_, s) = Fresh(); var a = Order(s); var b = Order(s); var p = Draft(s, a, b); Pack(s, p); var id = Apply(s, a, AfterSaleKind.CancelUnshipped);
    Assert(s.ShipmentDetails(p).Summary.AfterSaleHold); Throws<ConflictException>(() => s.DispatchShipment(p, Wh(s, p)));
    Throws<ConflictException>(() => s.ApproveAfterSale(id, Action(s, id))); Assert(Shirt(s).Reserved == 4);
    s.ApproveAfterSale(id, Action(s, id, true)); Assert(Shirt(s).Reserved == 2 && s.Details(b).Summary.State == ReviewState.Approved); Draft(s, b); Healthy(s);
}));
await Test("售后与出库并发仅一个成功，不留下已取消但发货的订单", async () =>
{
    var (_, s) = Fresh(); var o = Order(s); var p = Draft(s, o); Pack(s, p); var req = Request(s, o, AfterSaleKind.CancelUnshipped); var wh = Wh(s, p);
    Task<bool> Run(Action a) => Task.Run(() => { try { a(); return true; } catch (ConflictException) { return false; } });
    var winners = await Task.WhenAll(Run(() => s.CreateAfterSale(req)), Run(() => s.DispatchShipment(p, wh)));
    Assert(winners.Count(x => x) == 1); if (s.AfterSales().Total == 1) { s.ApproveAfterSale(s.AfterSales().Items[0].Id, Action(s, s.AfterSales().Items[0].Id, true)); Assert(Shirt(s).OnHand == 100); } else Assert(Shirt(s).OnHand == 98);
    Healthy(s);
});
await Test("来源售后编号去重，异请求和旧版本写入拒绝", () => Sync(() =>
{
    var (_, s) = Fresh(); var o = Order(s); Ship(s, o); var req = Request(s, o); var id = s.CreateAfterSale(req).Id;
    Assert(s.CreateAfterSale(req with { OperationId = Op() }).Replayed); Throws<ConflictException>(() => s.CreateAfterSale(req with { OperationId = Op(), RefundAmount = 5 }));
    Throws<ConflictException>(() => s.ApproveAfterSale(id, Action(s, id) with { ExpectedVersion = 0 })); Assert(s.AfterSales().Total == 1); Healthy(s);
}));
await Test("已出库单不得按未出库取消，未出库单不得申请退货", () => Sync(() =>
{
    var (_, s) = Fresh(); var a = Order(s); Throws<BusinessRuleException>(() => Apply(s, a)); Ship(s, a);
    Throws<ConflictException>(() => Apply(s, a, AfterSaleKind.CancelUnshipped)); Healthy(s);
}));
await Test("部分出库按实际出库行申请，不能退未发部分或别的订单", () => Sync(() =>
{
    var (_, s) = Fresh(); var o = Order(s, One(5)); Ship(s, o, 2); var req = Request(s, o, amount: 4, qty: 3);
    Throws<ConflictException>(() => s.CreateAfterSale(req)); var b = Order(s); Ship(s, b);
    Throws<BusinessRuleException>(() => s.CreateAfterSale(req with { Lines = Request(s, b).Lines }));
    var id = Apply(s, o, amount: 4, qty: 2); s.ApproveAfterSale(id, Action(s, id)); s.InspectReturn(id, Receipt(s, id, 2));
    Assert(Shirt(s).Reserved == 3 && s.Details(o).Summary.ShippingState == "Partial"); Healthy(s);
}));
await Test("多个申请累计数量和金额均不能超过原单", () => Sync(() =>
{
    var (_, s) = Fresh(); var o = Order(s); Ship(s, o); Apply(s, o, amount: 6, qty: 1);
    Throws<ConflictException>(() => Apply(s, o, amount: 5, qty: 1)); Throws<ConflictException>(() => Apply(s, o, amount: 4, qty: 2));
    Apply(s, o, amount: 4, qty: 1); Assert(s.PrepareAfterSale(o).Order.RemainingRefundAmount == 0); Healthy(s);
}));
await Test("并发申请同一退货数量只有一份占用成功", async () =>
{
    var (_, s) = Fresh(); var o = Order(s); Ship(s, o); var req = Request(s, o);
    var result = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Task.Run(() => { try { s.CreateAfterSale(req with { OperationId = Op(), SourceCaseNumber = Op() }); return true; } catch (ConflictException) { return false; } })));
    Assert(result.Count(x => x) == 1 && s.AfterSales().Total == 1); Healthy(s);
});
await Test("良品回主库存、残次品回隔离库存，退款不会使库存增加", () => Sync(() =>
{
    var (_, s) = Fresh(); var o = Order(s); Ship(s, o); var id = Apply(s, o); s.ApproveAfterSale(id, Action(s, id)); s.RecordRefund(id, Refund(s, id));
    Assert(Shirt(s).OnHand == 98 && Shirt(s).Quarantined == 0); s.InspectReturn(id, Receipt(s, id, 1, 1));
    Assert(Shirt(s).OnHand == 99 && Shirt(s).Available == 99 && Shirt(s).Quarantined == 1 && s.QuarantineLedger().Count == 1);
    s.CloseAfterSale(id, Action(s, id)); Healthy(s);
}));
await Test("仅退款不得收货入账，退款凭证和本地结案不变动库存", () => Sync(() =>
{
    var (_, s) = Fresh(); var o = Order(s); Ship(s, o); var id = Apply(s, o, AfterSaleKind.RefundOnly); s.ApproveAfterSale(id, Action(s, id));
    Throws<ConflictException>(() => s.InspectReturn(id, new(Op(), s.AfterSaleDetails(id).Summary.Version, Op(), "", [new("wrong", "sku-shirt", 1, 0)], "测试员", "错误入库")));
    s.RecordRefund(id, Refund(s, id)); s.CloseAfterSale(id, Action(s, id)); Assert(Shirt(s).OnHand == 98 && Shirt(s).Quarantined == 0); Healthy(s);
}));
await Test("组合商品按原组件快照收货，各组件质量可不同", () => Sync(() =>
{
    var (_, s) = Fresh(); var o = Order(s, SeedData.Samples().Single(x => x.Descriptor.Id == "taobao-bundle").Order); Ship(s, o); var id = Apply(s, o, amount: 10, qty: 1); s.ApproveAfterSale(id, Action(s, id));
    var d = s.AfterSaleDetails(id); Assert(d.Components.Single(c => c.SkuId == "sku-shirt").Expected == 1 && d.Components.Single(c => c.SkuId == "sku-mug").Expected == 2);
    s.InspectReturn(id, new(Op(), d.Summary.Version, Op(), "TEST", d.Components.Select(c => new InspectionLine(c.CaseLineId, c.SkuId, c.SkuId == "sku-shirt" ? c.Expected : 1, c.SkuId == "sku-shirt" ? 0 : 1)).ToList(), "测试员", "组合商品质检"));
    Assert(Shirt(s).OnHand == 99 && s.Inventory().Single(i => i.SkuId == "sku-mug").OnHand == 97 && s.Quarantine().Single(q => q.SkuId == "sku-mug").Quantity == 1); Healthy(s);
}));
await Test("部分批次收货后剩余量准确，超量和错 SKU 整批拒绝", () => Sync(() =>
{
    var (_, s) = Fresh(); var o = Order(s); Ship(s, o); var id = Apply(s, o); s.ApproveAfterSale(id, Action(s, id)); s.InspectReturn(id, Receipt(s, id));
    Assert(s.AfterSaleDetails(id).Components.Single().Remaining == 1); Throws<ConflictException>(() => s.InspectReturn(id, Receipt(s, id, 2)));
    var req = Receipt(s, id); Throws<ConflictException>(() => s.InspectReturn(id, req with { Lines = [req.Lines[0] with { SkuId = "sku-mug" }] }));
    s.InspectReturn(id, Receipt(s, id, 0, 1)); Assert(Shirt(s).OnHand == 99 && Shirt(s).Quarantined == 1); Healthy(s);
}));
await Test("收货操作键、业务批次号及重启共同防止重复入库", () => Sync(() =>
{
    var (db, s) = Fresh(); var o = Order(s); Ship(s, o); var id = Apply(s, o); s.ApproveAfterSale(id, Action(s, id)); var req = Receipt(s, id, 2); s.InspectReturn(id, req);
    var reopened = new OrderService(new CommerceDatabase(Path.GetDirectoryName(db.DatabasePath)!)); Assert(reopened.InspectReturn(id, req).Replayed && reopened.InspectReturn(id, req with { OperationId = Op() }).Replayed);
    Throws<ConflictException>(() => reopened.InspectReturn(id, req with { OperationId = Op(), Lines = [req.Lines[0] with { GoodQuantity = 1 }] })); Assert(Shirt(s).OnHand == 100 && s.AfterSaleDetails(id).Receipts.Count == 1); Healthy(s);
}));
await Test("并发收货不可超收或重复入库", async () =>
{
    var (_, s) = Fresh(); var o = Order(s); Ship(s, o); var id = Apply(s, o); s.ApproveAfterSale(id, Action(s, id)); var req = Receipt(s, id, 2);
    var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Task.Run(() => { try { s.InspectReturn(id, req with { OperationId = Op(), ReceiptNumber = Op() }); return true; } catch (ConflictException) { return false; } })));
    Assert(results.Count(x => x) == 1 && Shirt(s).OnHand == 100); Healthy(s);
});
await Test("质检事务异常不留下部分库存、组件数量或流水", () => Sync(() =>
{
    var (db, s) = Fresh(); var o = Order(s); Ship(s, o); var id = Apply(s, o); s.ApproveAfterSale(id, Action(s, id)); var req = Receipt(s, id, 1, 1);
    var broken = new OrderService(db, p => { if (p == "aftersale-after-stock") throw new IOException("测试中断"); }); Throws<IOException>(() => broken.InspectReturn(id, req));
    Assert(Shirt(s).OnHand == 98 && Shirt(s).Quarantined == 0 && s.AfterSaleDetails(id).Receipts.Count == 0 && s.AfterSaleDetails(id).Components.Single().Remaining == 2);
    s.InspectReturn(id, req); Healthy(s);
}));
await Test("退货扣账进程退出后恢复，无半入库", async () =>
{
    var (db, s) = Fresh(); var o = Order(s); Ship(s, o); var id = Apply(s, o); s.ApproveAfterSale(id, Action(s, id));
    var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true }; foreach (var arg in new[] { Assembly.GetExecutingAssembly().Location, "--crash-return", Path.GetDirectoryName(db.DatabasePath)!, id }) start.ArgumentList.Add(arg);
    using var p = Process.Start(start)!; using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)); await p.WaitForExitAsync(timeout.Token); Assert(p.ExitCode == 74);
    var recovered = new OrderService(new CommerceDatabase(Path.GetDirectoryName(db.DatabasePath)!)); Assert(Shirt(recovered).OnHand == 98 && recovered.AfterSaleDetails(id).Receipts.Count == 0); Healthy(recovered);
});
await Test("取消事务异常回滚出库单作废、订单状态和预占释放", () => Sync(() =>
{
    var (db, s) = Fresh(); var o = Order(s); var p = Draft(s, o); var id = Apply(s, o, AfterSaleKind.CancelUnshipped);
    var broken = new OrderService(db, x => { if (x == "aftersale-after-stock") throw new IOException(); }); Throws<IOException>(() => broken.ApproveAfterSale(id, Action(s, id)));
    Assert(s.ShipmentDetails(p).Summary.State == ShipmentState.Draft && s.Details(o).Summary.State == ReviewState.Approved && Shirt(s).Reserved == 2); Healthy(s);
}));
await Test("退款凭证须人工确认，未知状态不释放额度且禁止撤回", () => Sync(() =>
{
    var (_, s) = Fresh(); var o = Order(s); Ship(s, o); var id = Apply(s, o, AfterSaleKind.RefundOnly); s.ApproveAfterSale(id, Action(s, id));
    Throws<BusinessRuleException>(() => s.RecordRefund(id, Refund(s, id) with { ConfirmLocalEvidence = false }));
    s.RecordRefund(id, Refund(s, id, RefundTrackingState.Unknown)); Throws<ConflictException>(() => s.WithdrawAfterSale(id, Action(s, id))); Throws<ConflictException>(() => s.CloseAfterSale(id, Action(s, id)));
    Assert(s.PrepareAfterSale(o).Order.RemainingRefundAmount == 0 && Shirt(s).OnHand == 98); Healthy(s);
}));
await Test("退款金额、时间顺序和非法枚举在服务层验证", () => Sync(() =>
{
    var (_, s) = Fresh(); var o = Order(s); Ship(s, o); var id = Apply(s, o); s.ApproveAfterSale(id, Action(s, id));
    Throws<BusinessRuleException>(() => s.RecordRefund(id, Refund(s, id) with { Amount = 9.999m })); Throws<BusinessRuleException>(() => s.RecordRefund(id, Refund(s, id) with { Amount = 9 }));
    Throws<BusinessRuleException>(() => s.RecordRefund(id, Refund(s, id) with { Status = (RefundTrackingState)99 }));
    var req = Refund(s, id, RefundTrackingState.Unknown); s.RecordRefund(id, req);
    Throws<ConflictException>(() => s.RecordRefund(id, Refund(s, id) with { ObservedAt = req.ObservedAt.AddHours(-1) })); Healthy(s);
}));
await Test("退款事件、凭证号防重，已登记状态不可倒退", () => Sync(() =>
{
    var (_, s) = Fresh(); var o = Order(s); Ship(s, o); var id = Apply(s, o, amount: 5, qty: 1); s.ApproveAfterSale(id, Action(s, id)); var req = Refund(s, id); s.RecordRefund(id, req);
    Assert(s.RecordRefund(id, req with { OperationId = Op() }).Replayed); Throws<ConflictException>(() => s.RecordRefund(id, req with { OperationId = Op(), Evidence = "改内容" }));
    Throws<ConflictException>(() => s.RecordRefund(id, Refund(s, id, RefundTrackingState.Failed)));
    var second = Apply(s, o, amount: 5, qty: 1); s.ApproveAfterSale(second, Action(s, second)); Throws<ConflictException>(() => s.RecordRefund(second, Refund(s, second, reference: req.RefundReference)));
    Assert(s.AfterSaleDetails(id).RefundObservations.Count == 1); Healthy(s);
}));
await Test("退款记录提交异常整体回滚，原键可安全重试", () => Sync(() =>
{
    var (db, s) = Fresh(); var o = Order(s); Ship(s, o); var id = Apply(s, o); s.ApproveAfterSale(id, Action(s, id)); var req = Refund(s, id);
    var broken = new OrderService(db, x => { if (x == "aftersale-after-refund") throw new IOException(); }); Throws<IOException>(() => broken.RecordRefund(id, req));
    Assert(s.AfterSaleDetails(id).RefundObservations.Count == 0 && s.AfterSaleDetails(id).Summary.RefundState == RefundTrackingState.Pending);
    s.RecordRefund(id, req); Healthy(s);
}));
await Test("收到退货后不能撤回，少退结案须明确确认且不补虚假库存", () => Sync(() =>
{
    var (_, s) = Fresh(); var o = Order(s); Ship(s, o); var id = Apply(s, o); s.ApproveAfterSale(id, Action(s, id)); s.InspectReturn(id, Receipt(s, id));
    Throws<ConflictException>(() => s.WithdrawAfterSale(id, Action(s, id))); s.RecordRefund(id, Refund(s, id)); Throws<ConflictException>(() => s.CloseAfterSale(id, Action(s, id)));
    s.CloseAfterSale(id, Action(s, id, shortReturn: true)); Assert(Shirt(s).OnHand == 99 && s.AfterSaleDetails(id).Summary.ShortReturnAccepted);
    Throws<ConflictException>(() => s.InspectReturn(id, Receipt(s, id))); Healthy(s);
}));
await Test("没有收货时，即使退款凭证已登记也不能退货结案", () => Sync(() =>
{
    var (_, s) = Fresh(); var o = Order(s); Ship(s, o); var id = Apply(s, o); s.ApproveAfterSale(id, Action(s, id)); s.RecordRefund(id, Refund(s, id));
    Throws<ConflictException>(() => s.CloseAfterSale(id, Action(s, id, shortReturn: true))); Healthy(s);
}));
await Test("退款失败且尚未收货可撤回，重新申请恢复数量额度", () => Sync(() =>
{
    var (_, s) = Fresh(); var o = Order(s); Ship(s, o); var id = Apply(s, o); s.ApproveAfterSale(id, Action(s, id)); s.RecordRefund(id, Refund(s, id, RefundTrackingState.Failed)); s.WithdrawAfterSale(id, Action(s, id));
    Assert(s.PrepareAfterSale(o).Order.RemainingRefundAmount == 10 && s.PrepareAfterSale(o).Lines.Single().Remaining == 2); Apply(s, o); Healthy(s);
}));
await Test("隔离库存不能通过冻结策略变成可售库存", () => Sync(() =>
{
    var (_, s) = Fresh(); var o = Order(s); Ship(s, o); var id = Apply(s, o); s.ApproveAfterSale(id, Action(s, id)); s.InspectReturn(id, Receipt(s, id, 0, 2));
    var before = Shirt(s); s.Policy("sku-shirt", new(Op(), before.Version, 0, 0, "调整策略")); Assert(Shirt(s).OnHand == 98 && Shirt(s).Available == 98 && Shirt(s).Quarantined == 2); Healthy(s);
}));
await Test("退货组件精度不可表示时不创建半张售后单", async () =>
{
    var (db, s) = Fresh(); using var store = new WorkspaceStore(Path.GetDirectoryName(db.DatabasePath)!); var m = SeedData.Mappings().First(m => m.Platform == Platform.Douyin && m.InternalSkuId == "sku-shirt"); await store.SaveMappingAsync(new(m with { UnitMultiplier = .001m }, 1));
    var o = Order(s, One(1)); Ship(s, o); Throws<BusinessRuleException>(() => Apply(s, o, amount: 5, qty: .5m)); Assert(s.AfterSales().Total == 0); Healthy(s);
});
await Test("售后查询分页、状态、原订单搜索及越界输入", () => Sync(() =>
{
    var (_, s) = Fresh(); var o = Order(s); Ship(s, o); Apply(s, o); Assert(s.AfterSales(pageSize: 1, search: s.Details(o).Summary.ExternalOrderId, state: AfterSaleState.Open).Total == 1);
    Assert(s.AfterSales(search: "' OR 1=1--").Total == 0); Throws<BusinessRuleException>(() => s.AfterSales(page: 0)); Throws<BusinessRuleException>(() => s.AfterSales(state: (AfterSaleState)99)); Healthy(s);
}));
await Test("隔离和良品流水损坏可被核对检出，不自动修账", () => Sync(() =>
{
    var (db, s) = Fresh(); var o = Order(s); Ship(s, o); var id = Apply(s, o); s.ApproveAfterSale(id, Action(s, id)); s.InspectReturn(id, Receipt(s, id, 1, 1)); Healthy(s);
    db.Write(d => d.Execute("UPDATE QuarantineBalances SET QuantityUnits=0 WHERE SkuId='sku-shirt'")); Assert(!s.AuditAfterSales().Passed && !s.CheckConsistency().Passed);
}));
var report = new { passed = failed == 0, total = results.Count, failed, results }; Console.WriteLine($"TOTAL {results.Count} / FAILED {failed}");
if (args.Length > 0) { var path = Path.GetFullPath(args[0]); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, Json(report)); }
return failed == 0 ? 0 : 1;
