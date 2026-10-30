using System.Diagnostics;
using System.Reflection;
using CommerceErp.Core;
using CommerceErp.Infrastructure;
using static CommerceErp.Infrastructure.CommerceDatabase;

string Op() => Guid.NewGuid().ToString("N");
SampleOrder One(decimal quantity = 2, Platform platform = Platform.Douyin) => SeedData.BaseOrder(platform) with
{
    ExternalOrderId = Op(), ShippingFee = 0, OrderDiscount = 0, PaidAmount = 10,
    Lines = [SeedData.BaseOrder(platform).Lines[0] with { Quantity = quantity, GoodsAmount = 10, MerchantDiscount = 0 }]
};
WarehouseActionRequest Action(OrderService s, string id, string reason = "仓储测试") => new(Op(), s.ShipmentDetails(id).Summary.Version, "测试员", reason);
if (args.Length == 3 && args[0] == "--crash-dispatch")
{
    var s = new OrderService(new CommerceDatabase(args[1]), p => { if (p == "warehouse-after-stock") Environment.Exit(73); });
    s.DispatchShipment(args[2], Action(s, args[2])); return 2;
}
var root = Path.Combine(Path.GetTempPath(), "CommerceErp.WarehouseTests", Op()); Directory.CreateDirectory(root);
var results = new List<object>(); var failed = 0;
void Assert(bool value, string message = "断言失败") { if (!value) throw new InvalidOperationException(message); }
void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new InvalidOperationException("未抛出 " + typeof(T).Name); }
async Task Test(string name, Func<Task> test)
{
    try { await test(); results.Add(new { name, passed = true }); Console.WriteLine("PASS " + name); }
    catch (Exception e) { failed++; results.Add(new { name, passed = false, error = e.ToString() }); Console.WriteLine("FAIL " + name + " / " + e); }
}
Task Sync(Action a) { a(); return Task.CompletedTask; }
(CommerceDatabase Db, OrderService S) Fresh() { var db = new CommerceDatabase(Path.Combine(root, Op())); return (db, new(db)); }
ShippingAddress Address(string street = "测试路 100 号") => new("模拟收件人", "00000000000", "测试省", "测试市", "测试区", street);
string Order(OrderService s, SampleOrder? source = null, bool merge = true, string street = "测试路 100 号")
{
    var r = s.Import(new(Op(), source ?? One())); s.SaveShippingProfile(r.OrderId, new(Op(), r.Version, Address(street), merge, "测试员", "补充测试地址")); return r.OrderId;
}
List<AllocationLine> Allocate(OrderService s, params string[] orders) => orders.SelectMany(id =>
{
    var p = s.PrepareShipping(id); return p.Lines.Where(l => l.Remaining > 0).Select(l => new AllocationLine(id, l.ExternalLineId, l.Remaining, p.Order.Version));
}).ToList();
string Create(OrderService s, params string[] orders) => s.CreateShipment(new(Op(), Allocate(s, orders), "测试员", "测试配单")).Id;
void Pack(OrderService s, string id, bool waybill = true)
{
    s.StartPicking(id, Action(s, id));
    foreach (var check in s.ShipmentDetails(id).Checks) s.ScanShipment(id, new(Op(), s.ShipmentDetails(id).Summary.Version, check.SkuCode, check.Required, "测试员"));
    s.PackShipment(id, Action(s, id));
    if (waybill) s.SaveWaybill(id, new(Op(), s.ShipmentDetails(id).Summary.Version, "TEST-CARRIER", "TEST-" + Op(), "测试员", "登记测试运单"));
}
InventoryRow Shirt(OrderService s) => s.Inventory().Single(i => i.SkuId == "sku-shirt");
void Healthy(OrderService s) { var audit = s.CheckConsistency(); Assert(audit.Passed, string.Join("；", audit.Problems)); var w = s.AuditWarehouse(); Assert(w.Passed, string.Join("；", w.Problems)); }
void Queue(OrderService s, string id) { var job = s.Deliveries().Single(j => j.Id == id); s.RetryDelivery(id, new(Op(), job.Version, "测试员", "重试")); }

await Test("原 Stage2 数据库原样迁移，已有订单和库存不重置", () => Sync(() =>
{
    var dir = Path.Combine(root, Op()); Directory.CreateDirectory(dir); File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures/stage2-v1.db"), Path.Combine(dir, "commerce.db"));
    var db = new CommerceDatabase(dir); var s = new OrderService(db);
    Assert(db.Read(d => d.Scalar<long>("SELECT MAX(Version) FROM SchemaMigrations")) == 10);
    Assert(s.List().Total == 2 && Shirt(s).OnHand == 110.125m && Shirt(s).Reserved == 1 && s.Barcodes().Count == 2); Healthy(s);
}));
await Test("配单拣货封箱出库完整闭环，ATP 不因预占转实物扣减而变化", () => Sync(() =>
{
    var (_, s) = Fresh(); var order = Order(s); var id = Create(s, order);
    Assert(Shirt(s).OnHand == 100 && Shirt(s).Reserved == 2); Pack(s, id); var before = Shirt(s).Available;
    s.DispatchShipment(id, Action(s, id)); Assert(Shirt(s).OnHand == 98 && Shirt(s).Reserved == 0 && Shirt(s).Available == before);
    Assert(s.Details(order).Summary.ShippingState == "Complete" && s.Details(order).Reservations.Single().Consumed == 2);
    Assert(s.Deliveries().Single().State == "Blocked" && s.ShipmentDetails(id).Summary.State == ShipmentState.Shipped); Healthy(s);
}));
await Test("没有地址、未审核或取消订单不能创建出库单", () => Sync(() =>
{
    var (_, s) = Fresh(); var a = s.Import(new(Op(), One()));
    Throws<BusinessRuleException>(() => Create(s, a.OrderId));
    var b = s.Import(new(Op(), One(), false)); Throws<ConflictException>(() => s.PrepareShipping(b.OrderId));
    s.Cancel(a.OrderId, new(Op(), a.Version, "取消")); Throws<ConflictException>(() => s.PrepareShipping(a.OrderId)); Assert(s.Shipments().Total == 0); Healthy(s);
}));
await Test("重复配单幂等，异键超量或重复订单行被拒绝", () => Sync(() =>
{
    var (_, s) = Fresh(); var o = Order(s); var lines = Allocate(s, o); var req = new CreateShipmentRequest(Op(), lines, "测试员", "配单");
    var a = s.CreateShipment(req); Assert(s.CreateShipment(req).Replayed);
    Throws<ConflictException>(() => s.CreateShipment(req with { OperationId = Op() }));
    var current = s.PrepareShipping(o); Throws<ConflictException>(() => s.CreateShipment(new(Op(), [lines[0] with { ExpectedOrderVersion = current.Order.Version }], "测试员", "重复分配")));
    Throws<BusinessRuleException>(() => s.CreateShipment(new(Op(), [lines[0], lines[0]], "测试员", "重复行"))); Assert(s.Shipments().Total == 1); Healthy(s);
}));
await Test("并发配同一订单仅一个成功", async () =>
{
    var (_, s) = Fresh(); var o = Order(s); var lines = Allocate(s, o);
    var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() => { try { s.CreateShipment(new(Op(), lines, "测试员", "竞争配单")); return true; } catch (ConflictException) { return false; } })));
    Assert(results.Count(x => x) == 1 && s.Shipments().Total == 1); Healthy(s);
});
await Test("按订单行数量分包并部分出库，剩余数量可继续履约", () => Sync(() =>
{
    var (db, s) = Fresh(); var o = Order(s, One(5)); var p = s.PrepareShipping(o);
    var first = s.CreateShipment(new(Op(), [new(o, p.Lines[0].ExternalLineId, 2, p.Order.Version)], "测试员", "部分发货")).Id;
    Pack(s, first); s.DispatchShipment(first, Action(s, first));
    Assert(s.Details(o).Summary.ShippingState == "Partial" && Shirt(s).OnHand == 98 && Shirt(s).Reserved == 3 && s.PrepareShipping(o).Lines[0].Remaining == 3);
    var last = Create(s, o); Pack(s, last); s.DispatchShipment(last, Action(s, last));
    Assert(s.Details(o).Summary.ShippingState == "Complete" && Shirt(s).OnHand == 95 && Shirt(s).Reserved == 0 && s.Deliveries().Count == 2); Healthy(s);
    var payloads = s.Deliveries().Select(j => db.Read(d => FromJson<ShipmentNotification>(d.Scalar<string>("SELECT PayloadJson FROM DeliveryJobs WHERE Id=$id", ("id", j.Id))!))).ToList();
    Assert(payloads.All(p => p.IsPartial) && payloads.Sum(p => p.Lines.Sum(l => l.Quantity)) == 5, "最后一个分包仍须使用部分发货契约");
}));
await Test("拆单保留原单与行关联，不增减预占", () => Sync(() =>
{
    var (_, s) = Fresh(); var o = Order(s, One(5)); var id = Create(s, o); var d = s.ShipmentDetails(id);
    var req = new SplitShipmentRequest(Op(), d.Summary.Version, [new(d.Lines[0].Id, 2)], "测试员", "拆成两包"); var split = s.SplitShipment(id, req);
    Assert(split.RelatedIds.Count == 2 && s.ShipmentDetails(id).Summary.State == ShipmentState.Superseded);
    Assert(split.RelatedIds.Sum(i => s.ShipmentDetails(i).Lines.Sum(l => l.Quantity)) == 5 && Shirt(s).Reserved == 5 && s.PrepareShipping(o).Lines[0].Remaining == 0);
    Assert(s.SplitShipment(id, req).Replayed); Healthy(s);
}));
await Test("合单按同平台店铺及一致收货资料验证，重复行数量合并", () => Sync(() =>
{
    var (_, s) = Fresh(); var o = Order(s, One(5)); var id = Create(s, o); var d = s.ShipmentDetails(id);
    var split = s.SplitShipment(id, new(Op(), d.Summary.Version, [new(d.Lines[0].Id, 2)], "测试员", "拆分"));
    var merged = s.MergeShipments(new(Op(), split.RelatedIds.Select(i => new ShipmentVersion(i, s.ShipmentDetails(i).Summary.Version)).ToList(), "测试员", "合回"));
    Assert(s.ShipmentDetails(merged.Id).Lines.Single().Quantity == 5 && Shirt(s).Reserved == 5); Healthy(s);
}));
await Test("跨订单合包正确扣同 SKU 并分别生成原订单回传任务", () => Sync(() =>
{
    var (_, s) = Fresh(); var a = Order(s); var b = Order(s); var id = Create(s, a, b); Pack(s, id); s.DispatchShipment(id, Action(s, id));
    Assert(s.ShipmentDetails(id).Summary.OrderCount == 2 && Shirt(s).OnHand == 96 && s.Deliveries().Count == 2);
    Assert(s.Details(a).Summary.ShippingState == "Complete" && s.Details(b).Summary.ShippingState == "Complete"); Healthy(s);
}));
await Test("不同地址、不同平台及未允许合包均阻断且事务不留空单", () => Sync(() =>
{
    foreach (var mode in new[] { "address", "platform", "permission" })
    {
        var (_, s) = Fresh(); var a = Order(s); var b = Order(s, One(platform: mode == "platform" ? Platform.Jd : Platform.Douyin), mode != "permission", mode == "address" ? "不同路 2 号" : "测试路 100 号");
        Throws<BusinessRuleException>(() => Create(s, a, b)); Assert(s.Shipments().Total == 0); Healthy(s);
    }
}));
await Test("已录入运单和已开始拣货的单不可拆合", () => Sync(() =>
{
    var (_, s) = Fresh(); var id = Create(s, Order(s)); var d = s.ShipmentDetails(id);
    s.SaveWaybill(id, new(Op(), d.Summary.Version, "TEST", "TEST123456", "测试员", "运单"));
    Throws<ConflictException>(() => s.SplitShipment(id, new(Op(), s.ShipmentDetails(id).Summary.Version, [new(d.Lines[0].Id, 1)], "测试员", "拆分")));
    s.StartPicking(id, Action(s, id)); Throws<ConflictException>(() => s.SplitShipment(id, new(Op(), s.ShipmentDetails(id).Summary.Version, [new(d.Lines[0].Id, 1)], "测试员", "拆分"))); Healthy(s);
}));
await Test("错扫、超扫、漏扫不能封箱，扫码重试不累加", () => Sync(() =>
{
    var (_, s) = Fresh(); var id = Create(s, Order(s)); s.StartPicking(id, Action(s, id));
    Throws<BusinessRuleException>(() => s.ScanShipment(id, new(Op(), s.ShipmentDetails(id).Summary.Version, "MUG-BLUE", 1, "测试员")));
    Throws<BusinessRuleException>(() => s.ScanShipment(id, new(Op(), s.ShipmentDetails(id).Summary.Version, "UNKNOWN", 1, "测试员")));
    Throws<BusinessRuleException>(() => s.ScanShipment(id, new(Op(), s.ShipmentDetails(id).Summary.Version, "TS-WHITE-M", 3, "测试员")));
    var scan = new ScanShipmentRequest(Op(), s.ShipmentDetails(id).Summary.Version, "TS-WHITE-M", 1, "测试员"); s.ScanShipment(id, scan); Assert(s.ScanShipment(id, scan).Replayed);
    Throws<BusinessRuleException>(() => s.PackShipment(id, Action(s, id)));
    Assert(s.ShipmentDetails(id).Checks.Single().Verified == 1); Healthy(s);
}));
await Test("条码别名可扫描，条码不可重指向其他实物 SKU", () => Sync(() =>
{
    var (_, s) = Fresh(); s.SaveBarcode(new(Op(), 0, "6900000000001", "sku-shirt", "测试员", "登记测试条码"));
    Throws<ConflictException>(() => s.SaveBarcode(new(Op(), 1, "6900000000001", "sku-mug", "测试员", "错误映射")));
    Throws<BusinessRuleException>(() => s.SaveBarcode(new(Op(), 0, "6900000000002", "sku-kit", "测试员", "不能扫描组合虚拟 SKU")));
    var id = Create(s, Order(s)); s.StartPicking(id, Action(s, id)); s.ScanShipment(id, new(Op(), s.ShipmentDetails(id).Summary.Version, "6900000000001", 2, "测试员"));
    Assert(s.ShipmentDetails(id).Checks.Single().Verified == 2); Healthy(s);
}));
await Test("重置复核必须原因且保留历史，已封箱不允许重置", () => Sync(() =>
{
    var (_, s) = Fresh(); var id = Create(s, Order(s)); s.StartPicking(id, Action(s, id)); s.ScanShipment(id, new(Op(), s.ShipmentDetails(id).Summary.Version, "TS-WHITE-M", 2, "测试员"));
    Throws<BusinessRuleException>(() => s.ResetVerification(id, Action(s, id, ""))); s.ResetVerification(id, Action(s, id, "取错货重新核对"));
    Assert(s.ShipmentDetails(id).Checks.Single().Verified == 0 && s.ShipmentDetails(id).History.Any(h => h.Action == "Scan"));
    s.ScanShipment(id, new(Op(), s.ShipmentDetails(id).Summary.Version, "TS-WHITE-M", 2, "测试员")); s.PackShipment(id, Action(s, id));
    Throws<ConflictException>(() => s.ResetVerification(id, Action(s, id))); Healthy(s);
}));
await Test("没有运单不能出库，重复运单号不能用于另一有效包裹", () => Sync(() =>
{
    var (_, s) = Fresh(); var id = Create(s, Order(s)); Pack(s, id, false); Throws<BusinessRuleException>(() => s.DispatchShipment(id, Action(s, id)));
    s.SaveWaybill(id, new(Op(), s.ShipmentDetails(id).Summary.Version, "TEST", "TEST123456", "测试员", "运单"));
    var second = Create(s, Order(s)); Throws<ConflictException>(() => s.SaveWaybill(second, new(Op(), s.ShipmentDetails(second).Summary.Version, "TEST", "TEST123456", "测试员", "重复运单"))); Healthy(s);
}));
await Test("重复出库及并发出库不会重复扣实物、预占或增加回传任务", async () =>
{
    var (_, s) = Fresh(); var id = Create(s, Order(s)); Pack(s, id); var req = Action(s, id);
    await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => s.DispatchShipment(id, req with { OperationId = Op() }))));
    Assert(Shirt(s).OnHand == 98 && Shirt(s).Reserved == 0 && s.Deliveries().Count == 1 && s.StockLedger().Count(l => l.Action == "Ship") == 1); Healthy(s);
});
await Test("作废出库单仅退回配单量，取消原订单才释放预占", () => Sync(() =>
{
    var (_, s) = Fresh(); var o = Order(s); var id = Create(s, o); s.VoidShipment(id, Action(s, id));
    Assert(s.PrepareShipping(o).Lines.Single().Remaining == 2 && Shirt(s).Reserved == 2);
    var current = s.Details(o).Summary; s.Cancel(o, new(Op(), current.Version, "订单取消")); Assert(Shirt(s).Reserved == 0 && Shirt(s).OnHand == 100); Healthy(s);
}));
await Test("有出库单时不允许改地址或直接取消，已出库单不能作废", () => Sync(() =>
{
    var (_, s) = Fresh(); var o = Order(s); var id = Create(s, o); var current = s.Details(o).Summary;
    Throws<ConflictException>(() => s.Cancel(o, new(Op(), current.Version, "取消")));
    Throws<ConflictException>(() => s.SaveShippingProfile(o, new(Op(), current.Version, Address("改地址"), true, "测试员", "改地址")));
    Pack(s, id); s.DispatchShipment(id, Action(s, id)); Throws<ConflictException>(() => s.VoidShipment(id, Action(s, id)));
    Throws<ConflictException>(() => s.Cancel(o, new(Op(), s.Details(o).Summary.Version, "已出库取消"))); Healthy(s);
}));
await Test("组合商品分包按组件精确扣账并保留销售行数量", () => Sync(() =>
{
    var (_, s) = Fresh(); var o = Order(s, SeedData.Samples().Single(x => x.Descriptor.Id == "taobao-bundle").Order); var id = Create(s, o); var d = s.ShipmentDetails(id);
    var split = s.SplitShipment(id, new(Op(), d.Summary.Version, [new(d.Lines[0].Id, 1)], "测试员", "组合商品分包"));
    Pack(s, split.Id); s.DispatchShipment(split.Id, Action(s, split.Id));
    Assert(Shirt(s).OnHand == 99 && Shirt(s).Reserved == 1 && s.Inventory().Single(i => i.SkuId == "sku-mug").OnHand == 98 && s.Details(o).Summary.ShippingState == "Partial"); Healthy(s);
}));
await Test("拆分组件出现超过三位小数时整次拆分回滚", async () =>
{
    var (db, s) = Fresh(); using var store = new WorkspaceStore(Path.GetDirectoryName(db.DatabasePath)!);
    var map = SeedData.Mappings().First(m => m.Platform == Platform.Douyin && m.InternalSkuId == "sku-shirt"); await store.SaveMappingAsync(new(map with { UnitMultiplier = .001m }, 1));
    var o = Order(s, One(1)); var id = Create(s, o); var d = s.ShipmentDetails(id);
    Throws<BusinessRuleException>(() => s.SplitShipment(id, new(Op(), d.Summary.Version, [new(d.Lines[0].Id, .5m)], "测试员", "精度错误")));
    Assert(s.Shipments().Total == 1 && s.ShipmentDetails(id).Summary.State == ShipmentState.Draft); Healthy(s);
});
await Test("出库扣账中断整体回滚，原包裹仍可安全重试", () => Sync(() =>
{
    var (db, s) = Fresh(); var id = Create(s, Order(s)); Pack(s, id); var req = Action(s, id);
    var broken = new OrderService(db, p => { if (p == "warehouse-after-stock") throw new IOException("中断"); }); Throws<IOException>(() => broken.DispatchShipment(id, req));
    Assert(Shirt(s).OnHand == 100 && Shirt(s).Reserved == 2 && s.Deliveries().Count == 0 && s.ShipmentDetails(id).Summary.State == ShipmentState.Packed);
    s.DispatchShipment(id, req); Assert(Shirt(s).OnHand == 98); Healthy(s);
}));
await Test("出库事务内进程被终止，数据库恢复后无半扣账", async () =>
{
    var (db, s) = Fresh(); var id = Create(s, Order(s)); Pack(s, id);
    var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true };
    foreach (var argument in new[] { Assembly.GetExecutingAssembly().Location, "--crash-dispatch", Path.GetDirectoryName(db.DatabasePath)!, id }) start.ArgumentList.Add(argument);
    using var p = Process.Start(start)!; using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)); await p.WaitForExitAsync(timeout.Token); Assert(p.ExitCode == 73);
    var reopened = new OrderService(new CommerceDatabase(Path.GetDirectoryName(db.DatabasePath)!));
    Assert(Shirt(reopened).OnHand == 100 && Shirt(reopened).Reserved == 2 && reopened.ShipmentDetails(id).Summary.State == ShipmentState.Packed); Healthy(reopened);
});
await Test("平台没有连接器时明确阻断，回传重试不改变库存", async () =>
{
    var (db, s) = Fresh(); var id = Create(s, Order(s)); Pack(s, id); s.DispatchShipment(id, Action(s, id)); var job = s.Deliveries().Single(); Queue(s, job.Id);
    var worker = new ShipmentDeliveryProcessor(db, []); Assert(await worker.ProcessNextAsync()); Assert(s.Deliveries().Single().State == "Blocked" && Shirt(s).OnHand == 98); Healthy(s);
});
await Test("连接器不具备分包或合包能力时阻断发送", async () =>
{
    foreach (var merged in new[] { false, true })
    {
        var (db, s) = Fresh(); var o = Order(s); var p = s.PrepareShipping(o);
        var id = merged ? Create(s, o, Order(s)) : s.CreateShipment(new(Op(), [new(o, p.Lines[0].ExternalLineId, 1, p.Order.Version)], "测试员", "分包")).Id;
        Pack(s, id); s.DispatchShipment(id, Action(s, id)); foreach (var job in s.Deliveries()) Queue(s, job.Id);
        var gateway = new TestGateway { SupportsPartialShipments = false, SupportsMergedShipments = false };
        var worker = new ShipmentDeliveryProcessor(db, [gateway]); while (await worker.ProcessNextAsync()) { }
        Assert(s.Deliveries().All(j => j.State == "Blocked") && gateway.Sends == 0 && gateway.Queries == 0); Healthy(s);
    }
});
await Test("发送超时后先回查，远端已成功时不再次发送或扣库存", async () =>
{
    var (db, s) = Fresh(); var id = Create(s, Order(s)); Pack(s, id); s.DispatchShipment(id, Action(s, id)); var job = s.Deliveries().Single(); Queue(s, job.Id);
    var gateway = new TestGateway { LoseFirstResponse = true }; var worker = new ShipmentDeliveryProcessor(db, [gateway]); await worker.ProcessNextAsync();
    Assert(s.Deliveries().Single().State == "Unknown" && gateway.Sends == 1);
    Queue(s, job.Id); await worker.ProcessNextAsync(); Assert(s.Deliveries().Single().State == "Confirmed" && gateway.Sends == 1 && gateway.Queries == 2 && Shirt(s).OnHand == 98); Healthy(s);
});
await Test("明确拒绝进入人工处理队列，平台成功必须有回执", async () =>
{
    foreach (var invalidReceipt in new[] { false, true })
    {
        var (db, s) = Fresh(); var id = Create(s, Order(s)); Pack(s, id); s.DispatchShipment(id, Action(s, id)); Queue(s, s.Deliveries().Single().Id);
        var gateway = new TestGateway { Result = invalidReceipt ? new(GatewayOutcome.Confirmed, "缺回执") : new(GatewayOutcome.Rejected, "平台拒绝") };
        await new ShipmentDeliveryProcessor(db, [gateway]).ProcessNextAsync(); Assert(s.Deliveries().Single().State == (invalidReceipt ? "Unknown" : "Rejected")); Healthy(s);
    }
});
await Test("并发发送者只能取得一个任务租约，过期租约可恢复", async () =>
{
    var (db, s) = Fresh(); var id = Create(s, Order(s)); Pack(s, id); s.DispatchShipment(id, Action(s, id)); var job = s.Deliveries().Single(); Queue(s, job.Id);
    var gateway = new TestGateway { Delay = true }; var a = new ShipmentDeliveryProcessor(db, [gateway]); var b = new ShipmentDeliveryProcessor(db, [gateway]);
    var runs = await Task.WhenAll(a.ProcessNextAsync(), b.ProcessNextAsync()); Assert(runs.Count(x => x) == 1 && gateway.Sends == 1);
    db.Write(d => d.Execute("UPDATE DeliveryJobs SET State='Processing',LeaseToken='lost-lease',LeaseUntil='2000-01-01T00:00:00.0000000+00:00' WHERE Id=$id", ("id", job.Id)));
    Assert(await a.ProcessNextAsync()); Assert(s.Deliveries().Single().State == "Confirmed" && gateway.Sends == 1); Healthy(s);
});
await Test("配单与取消竞争不会留下已取消订单的有效出库单", async () =>
{
    var (_, s) = Fresh(); var o = Order(s); var lines = Allocate(s, o); var version = s.Details(o).Summary.Version;
    Task<bool> Run(Action a) => Task.Run(() => { try { a(); return true; } catch (ConflictException) { return false; } });
    var r = await Task.WhenAll(Run(() => s.CreateShipment(new(Op(), lines, "测试员", "竞争"))), Run(() => s.Cancel(o, new(Op(), version, "竞争取消"))));
    Assert(r.Count(x => x) == 1); Healthy(s);
});
await Test("查询筛选分页以及库存与出库一致性异常检测", () => Sync(() =>
{
    var (db, s) = Fresh(); var id = Create(s, Order(s)); Assert(s.Shipments(pageSize: 1, state: ShipmentState.Draft).Total == 1);
    Assert(s.Shipments(search: "' OR 1=1--").Total == 0); Throws<BusinessRuleException>(() => s.Shipments(page: 0));
    db.Write(d => d.Execute("UPDATE Reservations SET ConsumedUnits=1000 WHERE SkuId='sku-shirt'")); Assert(!s.AuditWarehouse().Passed && !s.CheckConsistency().Passed);
}));
await Test("不同 SKU 之间互抵的流水差异及缺失复核行仍能检出", () => Sync(() =>
{
    var (db, s) = Fresh(); var o = Order(s, SeedData.Samples().Single(x => x.Descriptor.Id == "taobao-bundle").Order); var id = Create(s, o);
    Pack(s, id); s.DispatchShipment(id, Action(s, id)); Healthy(s);
    db.Write(d => d.Execute("UPDATE InventoryLedger SET OnHandDelta=OnHandDelta+CASE WHEN SkuId='sku-shirt' THEN 1000 ELSE -1000 END WHERE ShipmentId=$id AND Action='Ship'", ("id", id)));
    Assert(!s.AuditWarehouse().Passed, "总数相等不能掩盖不同 SKU 错账");
    var (db2, s2) = Fresh(); var draft = Create(s2, Order(s2)); db2.Write(d => d.Execute("DELETE FROM ShipmentChecks WHERE ShipmentId=$id", ("id", draft)));
    Assert(!s2.AuditWarehouse().Passed, "组件没有复核需求行必须报告");
}));
var report = new { passed = failed == 0, total = results.Count, failed, results }; Console.WriteLine($"TOTAL {results.Count} / FAILED {failed}");
if (args.Length > 0) { var output = Path.GetFullPath(args[0]); Directory.CreateDirectory(Path.GetDirectoryName(output)!); File.WriteAllText(output, Json(report)); }
return failed == 0 ? 0 : 1;

sealed class TestGateway : IShipmentGateway
{
    public bool SupportsPartialShipments { get; init; } = true;
    public bool SupportsMergedShipments { get; init; } = true;
    public bool LoseFirstResponse { get; init; }
    public bool Delay { get; init; }
    public GatewayResult? Result { get; init; }
    public int Sends, Queries;
    private readonly HashSet<string> _applied = [];
    public bool CanHandle(Platform platform) => true;
    public async Task<GatewayResult> QueryAsync(ShipmentNotification notification, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Queries); if (Delay) await Task.Delay(75, cancellationToken);
        return _applied.Contains(notification.DeliveryId) ? new(GatewayOutcome.Confirmed, "回查已成功", "TEST-RECEIPT") : new(GatewayOutcome.NotFound, "确认不存在");
    }
    public Task<GatewayResult> SendAsync(ShipmentNotification notification, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Sends); _applied.Add(notification.DeliveryId);
        if (LoseFirstResponse && Sends == 1) throw new IOException("远端已写入但响应丢失");
        return Task.FromResult(Result ?? new GatewayResult(GatewayOutcome.Confirmed, "测试网关确认", "TEST-RECEIPT"));
    }
}
