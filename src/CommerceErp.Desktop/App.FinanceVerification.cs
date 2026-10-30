using System.IO;
using System.Security.Cryptography;
using System.Text;
using CommerceErp.Core;

namespace CommerceErp.Desktop;

public partial class App
{
    private static async Task VerifyFinanceAsync(MainWindow window,MainViewModel vm,LocalApiClient api,string directory,Func<string,Task> capture)
    {
        static void Check(bool value,string reason){if(!value)throw new InvalidOperationException("财务界面："+reason);}
        var f=vm.Finance;var after=vm.AfterSales.Details!.Summary;var orderId=after.OrderId;
        var original=await api.GetAsync<OrderDetails>("/api/orders/"+orderId);var ext=original.Summary.ExternalOrderId;
        var stockBefore=await api.GetAsync<List<InventoryRow>>("/api/inventory");
        vm.Navigate("finance");f.Actor="财务界面验证员";f.Reason="合成账单流程验收";f.Platform=f.Platforms.Single(p=>p.Id==Platform.Jd);f.BatchNumber="UI-FIN-001";f.FileName="界面合成账单.csv";
        f.Content=$"流水号,订单号,类型,金额,发生时间,币种,售后编号\r\nUI-IN1,{ext},收入,80.00,2026-10-01T10:00:00+08:00,CNY,\r\nUI-IN2,{ext},收入,31.00,2026-10-02T10:00:00+08:00,CNY,\r\nUI-RF,{ext},退款,111.00,2026-10-03T10:00:00+08:00,CNY,{after.SourceCaseNumber}\r\nUI-FEE,{ext},平台费,1.00,2026-10-03T10:00:00+08:00,CNY,\r\nUI-FRT,{ext},运费,2.00,2026-10-03T10:00:00+08:00,CNY,\r\n";
        await f.PreviewAsync();Check(f.Preview?.Valid==true&&f.Preview.NewRows==5,"账单预览数量");await capture("31-finance-preview");
        var blocked=false;f.FileName="修改文件名.csv";try{await f.ImportAsync();}catch(BusinessRuleException){blocked=true;}Check(blocked,"修改后须重新预览");await f.PreviewAsync();await f.ImportAsync();
        Check(f.Rows.Count==5,"导入流水");await capture("32-finance-rows");await f.OpenOrderAsync(orderId);f.Tab=2;Check(f.Details!.Order.Profit is null&&f.Details.Order.NetCost is null,"缺失成本不可算零");await capture("33-finance-differences");
        f.Tab=3;
        while(f.Costs.Any(c=>c.OrderId==orderId&&c.UnitCost is null))
        {f.SelectedCost=f.Costs.First(c=>c.OrderId==orderId&&c.UnitCost is null);f.HistoricalCost=f.SelectedCost.SkuId=="sku-shirt"?"5.00":"2.00";f.HistoricalEvidence="模拟历史采购单，界面验收";await f.FillCostAsync();}
        f.SelectedSku=f.SkuCosts.Single(s=>s.SkuId=="sku-shirt");f.CostValue="8.00";f.CostEvidence="未来出库成本，界面验收";await f.CostAsync();Check(f.Costs.Where(c=>c.OrderId==orderId&&c.SkuId=="sku-shirt").All(c=>c.UnitCost==5),"未来成本不能追改历史");await capture("34-finance-costs");
        await f.OpenOrderAsync(orderId);f.ExpectedFee="1.00";f.ExpectedFreight="2.00";f.FeeEvidence="模拟费用单，已检查没有其他费用";await f.FeesAsync();
        Check(f.Details!.Order.Profit==-5&&f.Details.Order.NetCost==2,"退款和残次成本核算");f.Tab=2;await capture("35-finance-reconciled");
        // Correct a source-order association with evidence, then void that erroneous extra batch.
        f.Tab=0;f.BatchNumber="UI-FIN-WRONG";f.Content="流水号,订单号,类型,金额,发生时间,币种,售后编号\r\nUI-WRONG,错误订单编号,收入,1.00,2026-10-01T10:00:00+08:00,CNY,\r\n";
        await f.PreviewAsync();await f.ImportAsync();var wrongBatch=f.SelectedBatch!.Source.Id;f.SelectedRow=f.Rows.Single(r=>r.Source.Transaction=="UI-WRONG");
        f.OpenRowCommand.Execute(null);await WaitFinance(f);Check(f.BillDetails is not null,"打开原始流水");f.MatchSearch=ext;await f.FindMatchOrdersAsync();f.MatchOrder=f.MatchOrders.Single(o=>o.Source.OrderId==orderId);f.MatchEvidence="模拟关联凭据";await f.MatchAsync();
        await f.OpenOrderAsync(orderId);Check(f.Details!.Order.Profit is null&&f.Details.Order.Income==112,"关联后收入差异应可见");
        f.SelectedBatch=f.Batches.Single(b=>b.Source.Id==wrongBatch);f.Reason="确认多导入一笔，作废合成批次";await f.VoidAsync();await f.OpenOrderAsync(orderId);Check(f.Details!.Order.Profit==-5,"作废应排除汇总");
        f.ExportNumber="UI-FIN-EXPORT-001";f.Target="本地财务交接验收";await f.CreateExportAsync();Check(f.ExportDocument?.Summary.State=="Prepared","仅生成快照");
        await f.SaveExportAsync(Path.Combine(directory,"finance-export.json"));Check(f.ExportDocument?.Summary.State=="Exported","本地保存记录");
        var bytes=await File.ReadAllBytesAsync(Path.Combine(directory,"finance-export.json"));Check(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()==f.ExportDocument!.Summary.Hash,"导出JSON哈希");
        var csv=await File.ReadAllTextAsync(Path.Combine(directory,"finance-export.csv"));Check(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(csv))).ToLowerInvariant()==f.ExportDocument.CsvHash,"导出CSV哈希");
        await capture("36-finance-reports");
        var stockAfter=await api.GetAsync<List<InventoryRow>>("/api/inventory");Check(stockBefore.All(b=>stockAfter.Any(a=>a.SkuId==b.SkuId&&a.OnHand==b.OnHand&&a.Reserved==b.Reserved&&a.Quarantined==b.Quarantined)),"财务流程不改变库存数量");
        Check((await api.GetAsync<FinanceAudit>("/api/finance/audit")).Passed,"财务结构核对");
        window.Width=1120;window.Height=760;for(var tab=0;tab<5;tab++){f.Tab=tab;await capture($"{37+tab}-finance-minimum-{tab+1}");}
        window.Width=1440;window.Height=920;f.Tab=2;
    }
    private static async Task WaitFinance(FinanceViewModel f){for(var i=0;i<100&&f.IsBusy;i++)await Task.Delay(30);if(f.IsBusy)throw new InvalidOperationException("财务界面命令超时");}
}
