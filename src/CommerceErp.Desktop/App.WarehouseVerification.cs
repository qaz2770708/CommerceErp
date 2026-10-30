using System.Globalization;
using System.IO;
using CommerceErp.Core;

namespace CommerceErp.Desktop;

public partial class App
{
    private static async Task VerifyWarehouseAsync(MainWindow window, MainViewModel vm, string directory, Func<string, Task> capture)
    {
        void Check(bool passed, string message) { if (!passed) throw new InvalidOperationException(message); }
        window.Width = 1440; window.Height = 920; vm.Navigate("warehouse"); var w = vm.Warehouse;
        await w.RefreshAsync(); w.SelectedCandidate = w.Candidates.Single(c => c.OrderId == vm.Operations.Details!.Summary.Id); await w.OpenSourceAsync();
        w.Actor = "仓储界面验证员"; w.Reason = "界面验证：保存收货资料"; w.Recipient = "模拟收件人"; w.Phone = "00000000000";
        w.Province = "测试省"; w.City = "测试市"; w.District = "测试区"; w.Street = "测试路 100 号"; w.AllowMerge = true;
        await w.SaveAddressAsync(); await w.AddCartAsync(); await capture("10-warehouse-allocation");
        Check(w.Cart.Count == 2, "应按两条原订单行配单"); w.Reason = "界面验证：配单"; await w.CreateAsync();
        Check(w.Details?.Summary.State == ShipmentState.Draft, "应生成待拣货出库单"); await capture("11-warehouse-draft");
        var originalId = w.Details!.Summary.Id; w.SplitLines[0].Quantity = "1"; w.Reason = "界面验证：分成两个包裹"; w.WorkTab = 1; await w.SplitAsync();
        Check(w.Shipments.Count(s => s.Source.State == ShipmentState.Draft) == 2 && w.Shipments.Single(s => s.Source.Id == originalId).Source.State == ShipmentState.Superseded, "拆分未正确保留原单");
        await capture("12-warehouse-split"); foreach (var row in w.Shipments.Where(s => s.Source.State == ShipmentState.Draft)) row.Selected = true;
        w.Reason = "界面验证：将两个包裹合回"; await w.MergeAsync(); Check(w.Details?.Lines.Count == 2, "合单后的原行数量不符");
        w.NewBarcode = "6901234500001"; w.BarcodeSku = w.Skus.Single(s => s.SkuId == "sku-shirt"); w.Reason = "界面验证：登记实物条码"; await w.SaveBarcodeAsync();
        w.Tab = 3; await capture("13-warehouse-barcodes"); w.Tab = 1; await w.ActAsync("pick");
        w.ScanCode = "UNREGISTERED"; w.ScanQuantity = "1"; var rejected = false;
        try { await w.ScanAsync(); } catch (BusinessRuleException) { rejected = true; }
        Check(rejected, "未登记条码必须拦截");
        foreach (var c in w.Details!.Checks.ToList())
        { w.ScanCode = c.SkuId == "sku-shirt" ? "6901234500001" : c.SkuCode; w.ScanQuantity = c.Required.ToString(CultureInfo.InvariantCulture); await w.ScanAsync(); }
        Check(w.Details.Checks.All(c => c.Required == c.Verified), "复核数量不符"); await capture("14-warehouse-verified");
        await File.WriteAllTextAsync(Path.Combine(directory, "picklist.html"), w.BuildPickListHtml());
        var original = w.Details; w.Details = original with { Address = original.Address with { Recipient = "<script>test</script>" } };
        Check(!w.BuildPickListHtml().Contains("<script>test</script>", StringComparison.Ordinal), "拣货单应转义用户输入"); w.Details = original;
        await w.ActAsync("pack"); w.Carrier = "模拟承运商"; w.Tracking = "TEST-UI-WAREHOUSE-001"; w.Reason = "界面验证：记录模拟运单"; await w.WaybillAsync();
        await vm.Operations.RefreshAsync(); var before = vm.Operations.Inventory.Single(i => i.SkuId == "sku-shirt").OnHand;
        w.Reason = "界面验证：确认出库"; await w.ActAsync("dispatch"); await vm.Operations.RefreshAsync();
        Check(w.Details?.Summary.State == ShipmentState.Shipped && vm.Operations.Inventory.Single(i => i.SkuId == "sku-shirt").OnHand == before - 1, "出库应扣减一件 T 恤");
        Check(w.Preparation?.Order.ShippingState == "Complete" && w.SourceLines.All(l => l.Source.Remaining == 0 && l.Source.Shipped == l.Source.Ordered), "配单页应立即刷新已出库和剩余数量");
        Check(vm.Operations.Inventory.All(i => i.Reserved == 0), "本轮订单全部出库后预占应归零"); await capture("15-warehouse-dispatched");
        w.Tab = 2; w.SelectedJob = w.Jobs.Single(); Check(w.SelectedJob.State == "Blocked", "无连接器不得显示回传成功");
        w.Reason = "界面验证：重试回传"; await w.RetryDeliveryAsync();
        for (var i = 0; i < 40; i++) { await Task.Delay(100); await w.RefreshAsync(); if (w.Jobs.Single().State == "Blocked" && w.Jobs.Single().Attempts > 0) break; }
        Check(w.Jobs.Single().State == "Blocked" && w.Jobs.Single().Attempts > 0, "未配置连接器应保持阻断");
        w.SelectedJob = w.Jobs.Single(); await w.AuditAsync(); await capture("16-warehouse-delivery-blocked");
        window.Width = 1120; window.Height = 760; w.Tab = 1; await capture("17-warehouse-minimum-size");
        w.Tab = 0; await capture("18-warehouse-allocation-minimum-size");
    }
}
