using System.Diagnostics;
using System.Reflection;
using CommerceErp.Core;
using CommerceErp.Infrastructure;
using static CommerceErp.Infrastructure.CommerceDatabase;

string Op() => Guid.NewGuid().ToString("N");
SampleOrder One(string? id = null, decimal quantity = 1) => SeedData.BaseOrder(Platform.Douyin) with
{
    ExternalOrderId = id ?? Op(), ShippingFee = 0, OrderDiscount = 0, PaidAmount = 10,
    Lines = [SeedData.BaseOrder(Platform.Douyin).Lines[0] with { Quantity = quantity, GoodsAmount = 10, MerchantDiscount = 0 }]
};
if (args.Length == 2 && args[0] == "--crash-reserve")
{
    var service = new OrderService(new CommerceDatabase(args[1]), point => { if (point == "after-first-reservation") Environment.Exit(73); });
    service.Import(new(Op(), One("crash-order"))); return 2;
}
var root = Path.Combine(Path.GetTempPath(), "CommerceErp.Stage2.Tests", Op());
Directory.CreateDirectory(root);
var results = new List<object>(); var failed = 0;
(CommerceDatabase Db, OrderService Service) Fresh() { var db = new CommerceDatabase(Path.Combine(root, Op())); return (db, new(db)); }
void Assert(bool condition, string message = "断言失败") { if (!condition) throw new InvalidOperationException(message); }
void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new InvalidOperationException("未抛出 " + typeof(T).Name); }
async Task Test(string name, Func<Task> action)
{
    try { await action(); results.Add(new { name, passed = true }); Console.WriteLine("PASS " + name); }
    catch (Exception e) { failed++; results.Add(new { name, passed = false, error = e.ToString() }); Console.WriteLine("FAIL " + name + " / " + e); }
}
Task Sync(Action action) { action(); return Task.CompletedTask; }
InventoryRow Shirt(OrderService s) => s.Inventory().Single(i => i.SkuId == "sku-shirt");
void Healthy(OrderService s) { var r = s.CheckConsistency(); Assert(r.Passed, string.Join(";", r.Problems)); }

await Test("订单审核和预占入同一事务，实物库存不变", () => Sync(() =>
{
    var (_, s) = Fresh(); var r = s.Import(new(Op(), One())); var d = s.Details(r.OrderId);
    Assert(r.State == ReviewState.Approved && r.Version == 2); Assert(Shirt(s).OnHand == 100 && Shirt(s).Reserved == 1 && Shirt(s).Available == 99);
    Assert(d.History.Count == 2 && d.Reservations.Single().Quantity == 1 && s.Dashboard().PendingOutbox == 2); Healthy(s);
}));
await Test("相同操作重放及相同业务订单换键均不重复预占", () => Sync(() =>
{
    var (_, s) = Fresh(); var request = new ImportOrderRequest(Op(), One()); var a = s.Import(request);
    Assert(s.Import(request).Replayed); var c = s.Import(request with { OperationId = Op() });
    Assert(c.Replayed && c.OrderId == a.OrderId && s.List().Total == 1 && Shirt(s).Reserved == 1); Healthy(s);
}));
await Test("同键异请求及异动作拒绝，同订单不同内容不覆盖", () => Sync(() =>
{
    var (_, s) = Fresh(); var req = new ImportOrderRequest(Op(), One()); var a = s.Import(req);
    Throws<ConflictException>(() => s.Import(req with { Order = One() }));
    Throws<ConflictException>(() => s.Cancel(a.OrderId, new(req.OperationId, a.Version, "测试")));
    Throws<ConflictException>(() => s.Import(new(Op(), req.Order with { SourceUpdatedAt = req.Order.SourceUpdatedAt.AddSeconds(1) })));
    Assert(Shirt(s).Reserved == 1); Healthy(s);
}));
await Test("非法结构、空订单和未知租户店铺不落业务表", () => Sync(() =>
{
    var (_, s) = Fresh();
    foreach (var o in new[] { One() with { Lines = null! }, One() with { PaidAmount = decimal.MaxValue }, One() with { ShopId = "other" }, One() with { TenantId = "other" }, One(quantity: 0), One() with { DataKind = "production" } })
        Throws<BusinessRuleException>(() => s.Import(new(Op(), o)));
    Throws<BusinessRuleException>(() => s.Import(new(Op(), null!)));
    Throws<BusinessRuleException>(() => s.Import(new("", One()))); Assert(s.List().Total == 0); Healthy(s);
}));
await Test("未知映射订单进入异常，补映射后重审成功", async () =>
{
    var (db, s) = Fresh(); var sample = SeedData.Samples().Single(x => x.Descriptor.Id == "pdd-unmapped").Order;
    var a = s.Import(new(Op(), sample)); Assert(a.State == ReviewState.Exception && s.Details(a.OrderId).Reservations.Count == 0);
    using var store = new WorkspaceStore(Path.GetDirectoryName(db.DatabasePath)!);
    await store.SaveMappingAsync(new(new(SeedData.Tenant, Platform.Pinduoduo, SeedData.Shop(Platform.Pinduoduo), "product-100", "variant-new", "sku-shirt", 1, 0), 0));
    var b = s.Review(a.OrderId, new(Op(), a.Version)); Assert(b.State == ReviewState.Approved); Healthy(s);
});
await Test("金额错误可更正并保留快照，未重审前不预占", () => Sync(() =>
{
    var (db, s) = Fresh(); var source = One(); var a = s.Import(new(Op(), source with { PaidAmount = 9.99m }));
    Assert(a.State == ReviewState.Exception);
    Throws<BusinessRuleException>(() => s.Correct(a.OrderId, new(Op(), a.Version, source, "")));
    var b = s.Correct(a.OrderId, new(Op(), a.Version, source, "修正模拟金额"));
    Assert(b.State == ReviewState.PendingReview && Shirt(s).Reserved == 0);
    var c = s.Review(a.OrderId, new(Op(), b.Version)); Assert(c.State == ReviewState.Approved);
    Assert(db.Read(d => d.Scalar<int>("SELECT COUNT(*) FROM OrderSnapshots")) == 4);
    Throws<ConflictException>(() => s.Correct(c.OrderId, new(Op(), c.Version, source, "不应允许"))); Healthy(s);
}));
await Test("未付款、退款和已发货进入挂起，已取消不占库存", () => Sync(() =>
{
    var (_, s) = Fresh();
    foreach (var source in new[] { One() with { PaymentState = PaymentState.Unpaid, PaidAmount = 0 }, One() with { PaymentState = PaymentState.Refunded }, One() with { FulfillmentState = FulfillmentState.Shipped } })
        Assert(s.Import(new(Op(), source)).State == ReviewState.OnHold);
    Assert(s.Import(new(Op(), One() with { FulfillmentState = FulfillmentState.Cancelled })).State == ReviewState.Cancelled);
    Assert(Shirt(s).Reserved == 0); Healthy(s);
}));
await Test("取消仅释放一次，重复导入不能使已取消订单复活", () => Sync(() =>
{
    var (_, s) = Fresh(); var source = One(); var a = s.Import(new(Op(), source)); var req = new OrderActionRequest(Op(), a.Version, "客户取消模拟单");
    var b = s.Cancel(a.OrderId, req); Assert(s.Cancel(a.OrderId, req).Replayed);
    Assert(s.Cancel(a.OrderId, new(Op(), b.Version, "再次取消")).Replayed);
    Assert(s.Import(new(Op(), source)).State == ReviewState.Cancelled);
    Throws<ConflictException>(() => s.Review(a.OrderId, new(Op(), b.Version)));
    Assert(Shirt(s).Reserved == 0 && Shirt(s).OnHand == 100 && s.StockLedger().Count(l => l.Action == "Release") == 1); Healthy(s);
}));
await Test("组合商品第二组件缺货时撤销第一组件预占和流水", () => Sync(() =>
{
    var (_, s) = Fresh(); var stock = Shirt(s); s.Policy(stock.SkuId, new(Op(), stock.Version, 99, 0, "仅留一件可售"));
    var source = SeedData.Samples().Single(x => x.Descriptor.Id == "taobao-bundle").Order;
    var a = s.Import(new(Op(), source)); Assert(a.State == ReviewState.Exception);
    Assert(s.Inventory().All(i => i.Reserved == 0) && s.Details(a.OrderId).Reservations.Count == 0 && s.StockLedger().All(l => l.Action != "Reserve")); Healthy(s);
}));
await Test("组合商品审核及取消按组件准确处理", () => Sync(() =>
{
    var (_, s) = Fresh(); var a = s.Import(new(Op(), SeedData.Samples().Single(x => x.Descriptor.Id == "taobao-bundle").Order));
    Assert(Shirt(s).Reserved == 2 && s.Inventory().Single(x => x.SkuId == "sku-mug").Reserved == 4);
    s.Cancel(a.OrderId, new(Op(), a.Version, "测试取消")); Assert(s.Inventory().All(i => i.Reserved == 0)); Healthy(s);
}));
await Test("20 个并发订单竞争 10 件可承诺量，仅 10 单成功", async () =>
{
    var (db, s) = Fresh(); s.Policy("sku-shirt", new(Op(), 1, 90, 0, "留十件用于并发竞争"));
    var services = Enumerable.Range(0, 20).Select(_ => new OrderService(new CommerceDatabase(Path.GetDirectoryName(db.DatabasePath)!))).ToArray();
    var tasks = services.Select((service, i) => Task.Run(() => service.Import(new(Op(), One("race-" + i))))).ToArray();
    var rows = await Task.WhenAll(tasks);
    Assert(rows.Count(r => r.State == ReviewState.Approved) == 10 && rows.Count(r => r.State == ReviewState.Exception) == 10);
    Assert(Shirt(s).Reserved == 10 && Shirt(s).Available == 0); Healthy(s);
});
await Test("20 个并发重复回调仅建一单并预占一次", async () =>
{
    var (_, s) = Fresh(); var source = One();
    var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => s.Import(new(Op(), source)))));
    Assert(results.Select(x => x.OrderId).Distinct().Count() == 1 && Shirt(s).Reserved == 1 && s.List().Total == 1); Healthy(s);
});
await Test("并发审核和取消由版本控制串行化，最终可完整释放", async () =>
{
    var (_, s) = Fresh(); var a = s.Import(new(Op(), One(), false));
    Task<bool> Run(Func<OrderCommandResult> f) => Task.Run(() => { try { f(); return true; } catch (ConflictException) { return false; } });
    var results = await Task.WhenAll(Run(() => s.Review(a.OrderId, new(Op(), a.Version))), Run(() => s.Cancel(a.OrderId, new(Op(), a.Version, "竞争取消"))));
    Assert(results.Count(x => x) == 1);
    var d = s.Details(a.OrderId); if (d.Summary.State != ReviewState.Cancelled) s.Cancel(a.OrderId, new(Op(), d.Summary.Version, "刷新后取消"));
    Assert(Shirt(s).Reserved == 0); Healthy(s);
});
await Test("事务中任意失败点不留下半订单、半预占或幂等记录", () => Sync(() =>
{
    foreach (var target in new[] { "after-first-reservation", "after-reservations", "before-commit" })
    {
        var (db, s) = Fresh(); var fail = new OrderService(db, p => { if (p == target) throw new IOException("模拟写入失败"); });
        Throws<IOException>(() => fail.Import(new(Op(), One())));
        Assert(s.List().Total == 0 && Shirt(s).Reserved == 0 && s.Dashboard().PendingOutbox == 0);
        Assert(db.Read(d => d.Scalar<int>("SELECT COUNT(*) FROM Operations")) == 0); Healthy(s);
    }
}));
await Test("取消处理中失败保持已审核和完整预占", () => Sync(() =>
{
    var (db, s) = Fresh(); var a = s.Import(new(Op(), SeedData.Samples().Single(x => x.Descriptor.Id == "taobao-bundle").Order));
    var fail = new OrderService(db, p => { if (p == "during-release") throw new IOException("释放中断"); });
    Throws<IOException>(() => fail.Cancel(a.OrderId, new(Op(), a.Version, "取消测试")));
    Assert(s.Details(a.OrderId).Summary.State == ReviewState.Approved && Shirt(s).Reserved == 2); Healthy(s);
}));
await Test("子进程预占后强制退出，重启通过日志回滚未提交事务", async () =>
{
    var (db, s) = Fresh();
    var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true };
    start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location); start.ArgumentList.Add("--crash-reserve"); start.ArgumentList.Add(Path.GetDirectoryName(db.DatabasePath)!);
    using var process = Process.Start(start)!; using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)); await process.WaitForExitAsync(timeout.Token);
    Assert(process.ExitCode == 73); var reopened = new OrderService(new CommerceDatabase(Path.GetDirectoryName(db.DatabasePath)!));
    Assert(reopened.List().Total == 0 && Shirt(reopened).Reserved == 0 && reopened.Dashboard().PendingOutbox == 0); Healthy(reopened);
});
await Test("重启后余额、订单、幂等键保留，补货不重复", () => Sync(() =>
{
    var (db, s) = Fresh(); var request = new ReceiveStockRequest(Op(), 12.345m, "模拟到货"); s.Receive("sku-shirt", request);
    var order = new ImportOrderRequest(Op(), One(quantity: 0.125m)); var first = s.Import(order);
    var reopened = new OrderService(new CommerceDatabase(Path.GetDirectoryName(db.DatabasePath)!));
    Assert(reopened.Receive("sku-shirt", request).Replayed && reopened.Import(order).Replayed);
    Assert(Shirt(reopened).OnHand == 112.345m && Shirt(reopened).Reserved == .125m && reopened.Details(first.OrderId).Summary.State == ReviewState.Approved); Healthy(reopened);
}));
await Test("冻结、安全库存和小数数量准确影响可承诺量", () => Sync(() =>
{
    var (_, s) = Fresh(); var p = s.Policy("sku-shirt", new(Op(), 1, 2.125m, 3.5m, "设置策略"));
    Assert(Shirt(s).Available == 94.375m);
    s.Import(new(Op(), One(quantity: 94.375m))); Assert(Shirt(s).Available == 0);
    Throws<ConflictException>(() => s.Policy("sku-shirt", new(Op(), p.Version, 0, 0, "旧版本")));
    var current = Shirt(s); Throws<BusinessRuleException>(() => s.Policy("sku-shirt", new(Op(), current.Version, 6, 0, "不能冻结已预占实物")));
    Throws<BusinessRuleException>(() => s.Receive("sku-shirt", new(Op(), .0001m, "精度错误"))); Healthy(s);
}));
await Test("缺货补货后重审成功且流水可追溯", () => Sync(() =>
{
    var (_, s) = Fresh(); var a = s.Import(new(Op(), One(quantity: 101))); Assert(a.State == ReviewState.Exception);
    var req = new ReceiveStockRequest(Op(), 1, "缺货补足"); s.Receive("sku-shirt", req);
    Throws<ConflictException>(() => s.Receive("sku-shirt", req with { Quantity = 2 }));
    var b = s.Review(a.OrderId, new(Op(), a.Version)); Assert(b.State == ReviewState.Approved && Shirt(s).Available == 0); Healthy(s);
}));
await Test("相同外部订单号在不同平台隔离，分页筛选正确", () => Sync(() =>
{
    var (_, s) = Fresh(); foreach (var p in Enum.GetValues<Platform>()) s.Import(new(Op(), SeedData.BaseOrder(p), false));
    Assert(s.List(pageSize: 2).Items.Count == 2 && s.List(pageSize: 2).Total == 4 && s.List(page: 2, pageSize: 2).Items.Count == 2);
    Assert(s.List(search: "000900719", state: ReviewState.PendingReview).Total == 4 && s.List(search: "' OR 1=1 --").Total == 0);
    Throws<BusinessRuleException>(() => s.List(page: -1)); Healthy(s);
}));
await Test("审核快照不受后续 SKU 映射修改影响", async () =>
{
    var (db, s) = Fresh(); var a = s.Import(new(Op(), One()));
    using var store = new WorkspaceStore(Path.GetDirectoryName(db.DatabasePath)!); var map = SeedData.Mappings().First(m => m.Platform == Platform.Douyin && m.InternalSkuId == "sku-shirt");
    await store.SaveMappingAsync(new(map with { UnitMultiplier = 3 }, 1));
    var b = s.Review(a.OrderId, new(Op(), a.Version)); Assert(b.Version == a.Version && Shirt(s).Reserved == 1 && s.Details(a.OrderId).Normalized!.Lines[0].MappingVersion == 1); Healthy(s);
});
await Test("第一阶段 JSON 只迁移一次且原文件保持不变", async () =>
{
    var dir = Path.Combine(root, Op()); Directory.CreateDirectory(dir); var mappings = SeedData.Mappings(); mappings[0] = mappings[0] with { UnitMultiplier = 2, Version = 2 };
    var legacy = new WorkspaceState(1, SeedData.Readiness(), mappings, []); var json = Json(legacy); File.WriteAllText(Path.Combine(dir, "workspace.json"), json);
    using var store = new WorkspaceStore(dir); Assert((await store.ReadAsync()).Catalog.Mappings.Any(m => m.Version == 2 && m.UnitMultiplier == 2));
    Assert(File.ReadAllText(Path.Combine(dir, "workspace.json")) == json);
    using var reopened = new WorkspaceStore(dir); Assert(new OrderService(reopened.Database).Inventory().All(i => i.OnHand == 100));
});
await Test("一致性检查能发现人为损坏的库存余额", () => Sync(() =>
{
    var (db, s) = Fresh(); db.Write(d => d.Execute("UPDATE InventoryBalances SET ReservedUnits=1000 WHERE SkuId='sku-shirt'"));
    Assert(!s.CheckConsistency().Passed && s.CheckConsistency().Problems.Count >= 2);
}));
var report = new { passed = failed == 0, total = results.Count, failed, results };
Console.WriteLine($"TOTAL {results.Count} / FAILED {failed}");
if (args.Length > 0) { var path = Path.GetFullPath(args[0]); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, Json(report)); }
return failed == 0 ? 0 : 1;
