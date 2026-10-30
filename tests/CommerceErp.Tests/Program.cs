using System.Text.Json;
using CommerceErp.Core;
using CommerceErp.Infrastructure;

var results = new List<object>();
var failed = 0;
var root = Path.Combine(Path.GetTempPath(), "CommerceErp.Stage1.Tests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
Catalog Catalog() => new(SeedData.Skus(), SeedData.Mappings());
SampleOrder Order() => SeedData.BaseOrder(Platform.Douyin);
ValidationReport Validate(SampleOrder? order = null, Catalog? catalog = null) => new OrderNormalizer().Validate(order ?? Order(), catalog ?? Catalog());
void Assert(bool condition, string message = "断言失败") { if (!condition) throw new InvalidOperationException(message); }
void Has(ValidationReport report, string code) => Assert(!report.Passed && report.Order is null && report.Issues.Any(i => i.Code == code), code);
T Read<T>(string json) => JsonSerializer.Deserialize<T>(json, ErpJson.Options)!;
string Json(object value) => JsonSerializer.Serialize(value, ErpJson.Options);
void Throws<T>(Action action) where T : Exception
{ try { action(); } catch (T) { return; } throw new InvalidOperationException("未抛出 " + typeof(T).Name); }
async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
{ try { await action(); } catch (T) { return; } throw new InvalidOperationException("未抛出 " + typeof(T).Name); }
async Task Test(string name, Func<Task> test)
{
    try { await test(); results.Add(new { name, passed = true }); Console.WriteLine("PASS " + name); }
    catch (Exception e) { failed++; results.Add(new { name, passed = false, error = e.Message }); Console.WriteLine("FAIL " + name + " / " + e.Message); }
}
Task Sync(Action action) { action(); return Task.CompletedTask; }

await Test("四平台正常订单统一为 111.00 元", () => Sync(() =>
{
    foreach (var p in Enum.GetValues<Platform>())
    { var r = Validate(SeedData.BaseOrder(p)); Assert(r.Passed && r.Order!.ExpectedPayable == 111m); }
}));
await Test("长订单号及前导零保留", () => Sync(() => Assert(Validate().Order!.ExternalOrderId == "000900719925474099312345678901")));
await Test("一分钱金额差异被阻断", () => Sync(() => Has(Validate(Order() with { PaidAmount = 110.99m }), "PAYMENT_MISMATCH")));
await Test("人民币金额三位小数被阻断", () => Sync(() => Has(Validate(Order() with { PaidAmount = 111.001m }), "INVALID_MONEY")));
await Test("0 数量被阻断", () => Sync(() => Has(Validate(Order() with { Lines = [Order().Lines[0] with { Quantity = 0 }] }), "INVALID_QUANTITY")));
await Test("负数数量被阻断", () => Sync(() => Has(Validate(Order() with { Lines = [Order().Lines[0] with { Quantity = -1 }] }), "INVALID_QUANTITY")));
await Test("重复订单行被阻断", () => Sync(() => Has(Validate(Order() with { Lines = [Order().Lines[0], Order().Lines[0]] }), "DUPLICATE_LINE")));
await Test("跨店铺映射不可复用", () => Sync(() => Has(Validate(Order() with { ShopId = "another-shop" }), "SKU_NOT_MAPPED")));
await Test("跨租户映射不可复用", () => Sync(() => Has(Validate(Order() with { TenantId = "another-tenant" }), "SKU_NOT_MAPPED")));
await Test("SKU 映射包含平台商品 ID", () => Sync(() => Has(Validate(Order() with { Lines = [Order().Lines[0] with { PlatformProductId = "another-product" }] }), "SKU_NOT_MAPPED")));
await Test("组合商品展开且不作为实物 SKU 扣减", () => Sync(() =>
{
    var r = Validate(SeedData.Samples().Single(s => s.Descriptor.Id == "taobao-bundle").Order);
    Assert(r.Passed); Assert(r.Order!.StockRequirements.Single(s => s.InternalSkuId == "sku-shirt").Quantity == 2);
    Assert(r.Order.StockRequirements.Single(s => s.InternalSkuId == "sku-mug").Quantity == 4);
    Assert(r.Order.StockRequirements.All(s => s.InternalSkuId != "sku-kit"));
}));
await Test("SKU 倍率进入库存需求", () => Sync(() =>
{
    var cat = Catalog(); var index = cat.Mappings.FindIndex(m => m.Platform == Platform.Douyin && m.InternalSkuId == "sku-shirt");
    cat.Mappings[index] = cat.Mappings[index] with { UnitMultiplier = 2.5m };
    Assert(Validate(catalog: cat).Order!.StockRequirements.Single(s => s.InternalSkuId == "sku-shirt").Quantity == 2.5m);
}));
await Test("换算导致四位小数时不静默舍入", () => Sync(() =>
{
    var cat = Catalog(); var index = cat.Mappings.FindIndex(m => m.Platform == Platform.Douyin && m.InternalSkuId == "sku-shirt");
    cat.Mappings[index] = cat.Mappings[index] with { UnitMultiplier = 0.001m };
    Has(Validate(Order() with { Lines = [Order().Lines[0] with { Quantity = 0.1m }, Order().Lines[1]] }, cat), "STOCK_QUANTITY_PRECISION");
}));
await Test("停用 SKU 不允许通过", () => Sync(() =>
{
    var cat = Catalog(); var index = cat.Mappings.FindIndex(m => m.Platform == Platform.Douyin && m.InternalSkuId == "sku-shirt");
    cat.Mappings[index] = cat.Mappings[index] with { InternalSkuId = "sku-retired" }; Has(Validate(catalog: cat), "SKU_UNAVAILABLE");
}));
await Test("重复有效映射被阻断", () => Sync(() =>
{ var cat = Catalog(); cat.Mappings.Add(cat.Mappings.First(m => m.Platform == Platform.Douyin)); Has(Validate(catalog: cat), "AMBIGUOUS_MAPPING"); }));
await Test("未付款模型可验证但附履约警告", () => Sync(() =>
{ var r = Validate(Order() with { PaymentState = PaymentState.Unpaid, PaidAmount = 0 }); Assert(r.Passed && r.Issues.Any(i => i.Code == "FULFILLMENT_HOLD")); }));
await Test("超额行优惠被阻断", () => Sync(() => Has(Validate(Order() with { Lines = [Order().Lines[0] with { MerchantDiscount = 90 }] }), "EXCESS_DISCOUNT")));
await Test("仅接受模拟数据", () => Sync(() => Has(Validate(Order() with { DataKind = "production" }), "SYNTHETIC_ONLY")));
await Test("非 CNY 明确阻断", () => Sync(() => Has(Validate(Order() with { Currency = "USD" }), "CURRENCY_NOT_SUPPORTED")));
await Test("null 行列表不导致崩溃", () => Sync(() => Has(Validate(Order() with { Lines = null! }), "INVALID_LINES")));
await Test("null 明细被阻断", () => Sync(() => Has(Validate(Order() with { Lines = [null!] }), "INVALID_LINE")));
await Test("极大非法金额不溢出", () => Sync(() => Has(Validate(Order() with { PaidAmount = decimal.MaxValue }), "INVALID_MONEY")));
await Test("必填金额缺失不能默认为零", () => Sync(() => Throws<JsonException>(() => Read<SampleOrder>(Json(Order()).Replace("\"shippingFee\": 8,", "")))));
await Test("未知字段（含原始个人信息字段）被拒绝", () => Sync(() => Throws<JsonException>(() => Read<SampleOrder>(Json(Order()).Replace("\"schemaVersion\": 1", "\"recipientName\": \"test\", \"schemaVersion\": 1")))));
await Test("时间缺少时区时拒绝", () => Sync(() => Throws<JsonException>(() => Read<SampleOrder>(Json(Order()).Replace("2026-10-28T10:00:00+08:00", "2026-10-28T10:00:00")))));
await Test("数字订单号不隐式转换", () => Sync(() => Throws<JsonException>(() => Read<SampleOrder>(Json(Order()).Replace("\"000900719925474099312345678901\"", "9007199254740993")))));
await Test("非法枚举不隐式转换", () => Sync(() => Throws<JsonException>(() => Read<SampleOrder>(Json(Order()).Replace("\"Douyin\"", "99")))));
await Test("相同输入产生稳定哈希及业务键", () => Sync(() =>
{ var a = Validate(); var b = Validate(); Assert(a.InputHash == b.InputHash && a.Order!.BusinessKey == b.Order!.BusinessKey && a.ReportId != b.ReportId); }));

await Test("已核实状态必须有依据", async () =>
{
    using var store = new WorkspaceStore(Path.Combine(root, "evidence"));
    var current = (await store.ReadAsync()).Readiness.First();
    current.Items[0] = current.Items[0] with { Status = CheckStatus.Verified };
    await ThrowsAsync<BusinessRuleException>(() => store.SaveReadinessAsync(current.Platform, new(0, current.Items)));
});
await Test("核实记录持久化并拒绝旧版本覆盖", async () =>
{
    var directory = Path.Combine(root, "readiness");
    using (var store = new WorkspaceStore(directory))
    {
        var current = (await store.ReadAsync()).Readiness.First();
        current.Items[0] = current.Items[0] with { Status = CheckStatus.InProgress, Evidence = "模拟核实中" };
        await store.SaveReadinessAsync(current.Platform, new(0, current.Items));
        await ThrowsAsync<ConflictException>(() => store.SaveReadinessAsync(current.Platform, new(0, current.Items)));
    }
    using var reopened = new WorkspaceStore(directory); Assert((await reopened.ReadAsync()).Readiness.First().Revision == 1);
});
await Test("两条并发新增同键映射仅一条成功", async () =>
{
    using var store = new WorkspaceStore(Path.Combine(root, "concurrent"));
    var map = SeedData.Mappings()[0] with { PlatformSkuId = "new-sku" };
    async Task<bool> Save() { try { await store.SaveMappingAsync(new(map, 0)); return true; } catch (ConflictException) { return false; } }
    var attempts = await Task.WhenAll(Save(), Save()); Assert(attempts.Count(x => x) == 1);
    Assert((await store.ReadAsync()).Catalog.Mappings.Count(m => m.PlatformSkuId == "new-sku") == 1);
});
await Test("映射编辑不改变历史报告快照", async () =>
{
    using var store = new WorkspaceStore(Path.Combine(root, "snapshot"));
    var report = await store.ValidateAsync(Order());
    var map = (await store.ReadAsync()).Catalog.Mappings.First(m => m.Platform == Platform.Douyin && m.InternalSkuId == "sku-shirt");
    await store.SaveMappingAsync(new(map with { UnitMultiplier = 2 }, 1));
    var updated = await store.ValidateAsync(Order());
    var history = await store.ReportsAsync();
    Assert(history.Single(r => r.ReportId == report.ReportId).Order!.Lines[0].UnitMultiplier == 1);
    Assert(updated.Order!.Lines[0].UnitMultiplier == 2 && updated.Order.Lines[0].MappingVersion == 2);
});
await Test("两个服务实例共享数据库并由版本控制防止覆盖", () => Sync(() =>
{
    var directory = Path.Combine(root, "lease"); using var first = new WorkspaceStore(directory);
    using var second = new WorkspaceStore(directory);
    var map = SeedData.Mappings()[0];
    first.SaveMappingAsync(new(map with { UnitMultiplier = 2 }, 1)).GetAwaiter().GetResult();
    Throws<ConflictException>(() => second.SaveMappingAsync(new(map with { UnitMultiplier = 3 }, 1)).GetAwaiter().GetResult());
}));
await Test("损坏工作区不静默重置", () => Sync(() =>
{
    var directory = Path.Combine(root, "corrupt"); Directory.CreateDirectory(directory);
    var path = Path.Combine(directory, "workspace.json"); File.WriteAllText(path, "corrupted");
    Throws<JsonException>(() => { using var store = new WorkspaceStore(directory); }); Assert(File.ReadAllText(path) == "corrupted");
}));
await Test("所有平台默认待核实且真实连接关闭", async () =>
{
    using var store = new WorkspaceStore(Path.Combine(root, "default")); var data = await store.ReadAsync();
    Assert(!data.LiveConnectionsEnabled && data.Readiness.All(r => r.Items.All(i => i.Status == CheckStatus.NotChecked)));
});

var result = new { passed = failed == 0, total = results.Count, failed, results };
Console.WriteLine($"TOTAL {results.Count} / FAILED {failed}");
if (args.Length > 0) { var output = Path.GetFullPath(args[0]); Directory.CreateDirectory(Path.GetDirectoryName(output)!); await File.WriteAllTextAsync(output, Json(result)); }
return failed == 0 ? 0 : 1;
