using System.Globalization;
using CommerceErp.Core;

namespace CommerceErp.Desktop;

public partial class App
{
    private static async Task VerifyAfterSalesAsync(MainWindow window, MainViewModel vm, LocalApiClient api, Func<string, Task> capture)
    {
        void Check(bool passed, string message) { if (!passed) throw new InvalidOperationException(message); }
        window.Width = 1440; window.Height = 920; vm.Navigate("after-sales"); var a = vm.AfterSales;
        await a.RefreshAsync(); a.Actor = "售后界面验证员"; a.Reason = "界面验证：申请退货";
        a.SelectedCandidate = a.Candidates.Single(c => c.OrderId == vm.Operations.Details!.Summary.Id); await a.OpenOrderAsync();
        a.Kind = a.Kinds.Single(k => k.Value == AfterSaleKind.ReturnRefund); a.SourceNumber = "LOCAL-UI-RETURN-001";
        foreach (var l in a.SourceLines) l.Quantity = l.Source.Remaining.ToString(CultureInfo.InvariantCulture);
        await capture("19-after-sale-application"); await a.CreateAsync();
        Check(a.Details?.Summary.State == AfterSaleState.Open && a.Details.Lines.Count == 2, "应创建两条原出库行的售后");
        var returnedCase = a.Details!.Summary.Id; await capture("20-after-sale-review");
        a.Reason = "界面验证：批准退货"; await a.ActAsync("approve"); Check(a.CanInspect && !a.ApproveCommand.CanExecute(null), "批准后才能质检，不能重复批准");
        await vm.Operations.RefreshAsync(); var shirtBefore = vm.Operations.Inventory.Single(i => i.SkuId == "sku-shirt").OnHand; var mugBefore = vm.Operations.Inventory.Single(i => i.SkuId == "sku-mug").OnHand;
        foreach (var l in a.Inspection) { l.Good = l.Source.SkuId == "sku-shirt" ? "1" : "0"; l.Damaged = l.Source.SkuId == "sku-mug" ? "1" : "0"; }
        a.ReceiptNumber = "RCV-UI-001"; a.ReturnTracking = "TEST-RETURN-UI-001"; a.Reason = "界面验证：T恤良品，杯子残次";
        await capture("21-after-sale-inspection"); await a.InspectAsync(); await vm.Operations.RefreshAsync();
        Check(vm.Operations.Inventory.Single(i => i.SkuId == "sku-shirt").OnHand == shirtBefore + 1 && vm.Operations.Inventory.Single(i => i.SkuId == "sku-mug").OnHand == mugBefore, "良品与残次品不可混入主库存");
        Check(a.Quarantine.Single(q => q.SkuId == "sku-mug").Quantity == 1 && a.Details!.Components.All(c => c.Remaining == 0), "隔离数量和剩余收货不符");
        a.Tab = 2; a.RefundState = a.RefundStates.Single(s => s.Value == RefundTrackingState.Unknown); a.Evidence = "界面验证：模拟退款响应不明，需要人工回查"; a.Reason = "界面验证：记录不明状态"; await a.RefundAsync();
        Check(a.Details!.Summary.RefundState == RefundTrackingState.Unknown && !a.CanWithdraw, "结果不明不能撤回释放额度");
        a.RefundState = a.RefundStates.Single(s => s.Value == RefundTrackingState.EvidenceRecorded); a.RefundReference = "TEST-REFUND-UI-001"; a.Evidence = "模拟人工凭证，仅界面验收，未经平台核验";
        var blocked = false; try { await a.RefundAsync(); } catch (BusinessRuleException) { blocked = true; } Check(blocked, "人工登记须确认来源边界");
        a.ConfirmEvidence = true; await a.RefundAsync(); await capture("22-after-sale-refund-evidence");
        Check(a.Details!.Summary.RefundState == RefundTrackingState.EvidenceRecorded && !a.CanRecordRefund && a.Refunds.Count == 2, "退款历史与不可重复登记状态不符");
        a.Tab = 1; a.Reason = "界面验证：退货齐全，凭证本地结案"; await a.ActAsync("close"); Check(a.Details!.Summary.State == AfterSaleState.Closed && !a.CanInspect, "结案后不能再次收货"); await capture("23-after-sale-closed");
        a.Tab = 3; await capture("24-after-sale-quarantine");

        var order = SeedData.BaseOrder(Platform.Douyin) with { ExternalOrderId = "UI-CANCEL-" + Guid.NewGuid().ToString("N") };
        var accepted = await api.PostCommandAsync<OrderCommandResult>("/api/orders", new { order, autoReview = true });
        var w = vm.Warehouse; await w.RefreshAsync(); w.SelectedCandidate = w.Candidates.Single(c => c.OrderId == accepted.OrderId); await w.OpenSourceAsync();
        w.Reason = "售后界面验证：拣货前置"; w.Recipient = "模拟用户"; w.Phone = "00000000000"; w.Province = "测试省"; w.City = "测试市"; w.District = "测试区"; w.Street = "测试路";
        await w.SaveAddressAsync(); await w.AddCartAsync(); await w.CreateAsync(); var package = w.Details!.Summary.Id; await w.ActAsync("pick");
        await a.RefreshAsync(); a.SelectedCandidate = a.Candidates.Single(c => c.OrderId == accepted.OrderId); a.Kind = a.Kinds[0]; await a.OpenOrderAsync(); a.SourceNumber = "LOCAL-UI-CANCEL-001"; a.Reason = "界面验证：拣货中申请整单取消"; await a.CreateAsync();
        vm.Navigate("warehouse"); await w.OpenShipmentAsync(package); Check(w.Details!.Summary.AfterSaleHold, "仓储详情应显示售后拦截"); await capture("25-warehouse-after-sale-hold");
        vm.Navigate("after-sales"); blocked = false; try { await a.ActAsync("approve"); } catch (BusinessRuleException) { blocked = true; } Check(blocked, "拣货中取消必须确认实物归还");
        a.ConfirmReturned = true; a.Reason = "界面验证：确认整包实物归还，批准取消"; await a.ActAsync("approve");
        var cancelled = await api.GetAsync<OrderDetails>("/api/orders/" + accepted.OrderId); Check(cancelled.Summary.State == ReviewState.Cancelled && cancelled.Reservations.All(r => r.Quantity == 0), "取消需释放原订单预占");
        Check((await api.GetAsync<ShipmentDetails>("/api/warehouse/shipments/" + package)).Summary.State == ShipmentState.Voided, "相关包裹应作废"); await capture("26-after-sale-cancelled");
        a.RefundState = a.RefundStates[2]; a.ConfirmEvidence = true; a.RefundReference = "TEST-REFUND-UI-002"; a.Evidence = "模拟取消退款凭证，未经平台核验"; a.ObservedAt = DateTimeOffset.Now.ToString("O"); await a.RefundAsync(); await a.ActAsync("close");
        await a.OpenCaseAsync(returnedCase); await a.AuditAsync(); Check(a.Message.StartsWith("检查通过", StringComparison.Ordinal), "售后库存核对失败");
        window.Width = 1120; window.Height = 760; a.Tab = 1; await capture("27-after-sale-minimum-size"); a.Tab = 2; await capture("28-refund-minimum-size"); a.Tab = 0; await capture("29-after-sale-application-minimum-size");
        window.Width = 1440; window.Height = 920; vm.Navigate("inventory"); await vm.Operations.RefreshAsync(); await capture("30-inventory-with-quarantine");
    }
}
