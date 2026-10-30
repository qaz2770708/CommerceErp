using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Input;
using CommerceErp.Core;
using Microsoft.Win32;

namespace CommerceErp.Desktop;

public sealed record FinanceOrderRow(FinanceOrder Source)
{ public string State=>Source.Problems.Count==0?"本地对平（未平台核验）":$"待处理 {Source.Problems.Count} 项"; public string Profit=>Source.Profit?.ToString("0.00")??"待完善"; }
public sealed record FinanceBillRow(BillRow Source)
{ public string Kind=>Source.Kind switch {BillKind.Sale=>"收入",BillKind.Refund=>"退款",BillKind.PlatformFee=>"平台费",_=>"运费"}; }
public sealed record FinanceBatchRow(BillBatch Source){public string Label=>$"{Source.Number} · {(Source.State=="Active"?"有效":"作废")} · {Source.NewRows} 笔";}
public sealed record FinanceExportRow(FinanceExport Source){public string State=>Source.State=="Prepared"?"已生成，待保存文件":"已本地导出，未记账";}
public sealed class FinanceColumn(string label,string value):ObservableObject
{public string Label{get;}=label;private string _value=value;public string Value{get=>_value;set=>Set(ref _value,value);}}

public sealed class FinanceViewModel(LocalApiClient api):ObservableObject
{
    public ObservableCollection<FinanceBatchRow> Batches{get;}=[];
    public ObservableCollection<FinanceBillRow> Rows{get;}=[];
    public ObservableCollection<FinanceOrderRow> Orders{get;}=[];
    public ObservableCollection<FinanceOrderRow> MatchOrders{get;}=[];
    public ObservableCollection<AfterSaleSummary> MatchCases{get;}=[];
    public ObservableCollection<SkuCost> SkuCosts{get;}=[];
    public ObservableCollection<CostSnapshot> Costs{get;}=[];
    public ObservableCollection<FinanceExportRow> Exports{get;}=[];
    public ObservableCollection<PlatformInfo> Platforms{get;}=[];
    public ObservableCollection<ShopProfile> Shops{get;}=[];
    private List<ShopProfile> _shopOptions=[];private ShopProfile? _selectedShop;
    public ShopProfile? SelectedShop{get=>_selectedShop;set{Set(ref _selectedShop,value);Changed(nameof(ShopId));}}
    private void RefreshShops(){var id=SelectedShop?.Id;Shops.Clear();foreach(var shop in _shopOptions.Where(s=>s.Platform==Platform.Id))Shops.Add(shop);SelectedShop=Shops.FirstOrDefault(s=>s.Id==id)??Shops.FirstOrDefault();}
    public List<FinanceColumn> Columns{get;}=[new("流水编号列","流水号"),new("订单编号列","订单号"),new("交易类型列","类型"),new("元金额列","金额"),new("发生时间列","发生时间"),new("币种列","币种"),new("来源售后列","售后编号")];
    private bool _busy,_unmatched,_differences;private int _tab,_rowPage=1,_orderPage=1,_rowTotal,_orderTotal;
    public bool IsBusy{get=>_busy;set{Set(ref _busy,value);Changed(nameof(IsReady));CommandManager.InvalidateRequerySuggested();}}
    public bool IsReady=>!IsBusy;
    public int Tab{get=>_tab;set=>Set(ref _tab,value);}
    public bool Unmatched{get=>_unmatched;set=>Set(ref _unmatched,value);}
    public bool Differences{get=>_differences;set=>Set(ref _differences,value);}
    private PlatformInfo? _platform;
    public PlatformInfo Platform{get=>_platform??Platforms.FirstOrDefault()??SeedData.Platforms[0];set{if(value is null)return;Set(ref _platform,value);RefreshShops();Changed(nameof(ShopId));}}
    public string ShopId=>SelectedShop?.Id??"";
    private string _actor=Environment.UserName,_reason="",_message="账单手工导入；先预览再提交。利润缺少成本或费用时显示待完善。";
    public string Actor{get=>_actor;set=>Set(ref _actor,value);}public string Reason{get=>_reason;set=>Set(ref _reason,value);}public string Message{get=>_message;set=>Set(ref _message,value);}
    private string _batch=Number("BILL"),_file="statement.csv",_content="",_saleTypes="收入|Sale",_refundTypes="退款|Refund",_feeTypes="平台费|PlatformFee",_freightTypes="运费|Freight";
    public string BatchNumber{get=>_batch;set=>Set(ref _batch,value);}public string FileName{get=>_file;set=>Set(ref _file,value);}public string Content{get=>_content;set=>Set(ref _content,value);}
    public string SaleTypes{get=>_saleTypes;set=>Set(ref _saleTypes,value);}public string RefundTypes{get=>_refundTypes;set=>Set(ref _refundTypes,value);}public string FeeTypes{get=>_feeTypes;set=>Set(ref _feeTypes,value);}public string FreightTypes{get=>_freightTypes;set=>Set(ref _freightTypes,value);}
    private BillPreview? _preview;private string _previewInput="";
    public BillPreview? Preview{get=>_preview;set{Set(ref _preview,value);Changed(nameof(PreviewText));}}
    public string PreviewText=>Preview is null?"尚未预览":Preview.Valid?$"校验通过：新增 {Preview.NewRows} 笔，重复 {Preview.DuplicateRows} 笔。修改内容后须重新预览。":string.Join("；",Preview.Errors);
    private string _search="",_orderSearch="",_matchSearch="",_evidence="",_costValue="",_costEvidence="",_historicalValue="",_historicalEvidence="",_fee="",_freight="",_feeEvidence="",_exportNumber=Number("FIN"),_target="通用财务交接（未连接外部系统）";
    public string Search{get=>_search;set=>Set(ref _search,value);}public string OrderSearch{get=>_orderSearch;set=>Set(ref _orderSearch,value);}public string MatchSearch{get=>_matchSearch;set=>Set(ref _matchSearch,value);}public string MatchEvidence{get=>_evidence;set=>Set(ref _evidence,value);}
    public string CostValue{get=>_costValue;set=>Set(ref _costValue,value);}public string CostEvidence{get=>_costEvidence;set=>Set(ref _costEvidence,value);}public string HistoricalCost{get=>_historicalValue;set=>Set(ref _historicalValue,value);}public string HistoricalEvidence{get=>_historicalEvidence;set=>Set(ref _historicalEvidence,value);}
    public string ExpectedFee{get=>_fee;set=>Set(ref _fee,value);}public string ExpectedFreight{get=>_freight;set=>Set(ref _freight,value);}public string FeeEvidence{get=>_feeEvidence;set=>Set(ref _feeEvidence,value);}
    public string ExportNumber{get=>_exportNumber;set=>Set(ref _exportNumber,value);}public string Target{get=>_target;set=>Set(ref _target,value);}
    private FinanceBatchRow? _selectedBatch;public FinanceBatchRow? SelectedBatch{get=>_selectedBatch;set=>Set(ref _selectedBatch,value);}
    private FinanceBillRow? _selectedRow;public FinanceBillRow? SelectedRow{get=>_selectedRow;set{Set(ref _selectedRow,value);BillDetails=null;}}
    private BillDetails? _billDetails;public BillDetails? BillDetails{get=>_billDetails;set=>Set(ref _billDetails,value);}
    private FinanceOrderRow? _selectedOrder;public FinanceOrderRow? SelectedOrder{get=>_selectedOrder;set{Set(ref _selectedOrder,value);if(value?.Source.OrderId!=Details?.Order.OrderId)Details=null;}}
    private FinanceOrderRow? _matchOrder;public FinanceOrderRow? MatchOrder{get=>_matchOrder;set{Set(ref _matchOrder,value);MatchCases.Clear();MatchCase=null;}}
    private AfterSaleSummary? _matchCase;public AfterSaleSummary? MatchCase{get=>_matchCase;set=>Set(ref _matchCase,value);}
    private SkuCost? _sku;public SkuCost? SelectedSku{get=>_sku;set{Set(ref _sku,value);CostValue=value?.UnitCost?.ToString(CultureInfo.InvariantCulture)??"";CostEvidence=value?.Evidence??"";}}
    private CostSnapshot? _snapshot;public CostSnapshot? SelectedCost{get=>_snapshot;set{Set(ref _snapshot,value);HistoricalCost="";HistoricalEvidence="";}}
    private FinanceExportRow? _export;public FinanceExportRow? SelectedExport{get=>_export;set{Set(ref _export,value);ExportDocument=null;}}
    private FinanceExportDocument? _document;public FinanceExportDocument? ExportDocument{get=>_document;set{Set(ref _document,value);Changed(nameof(ExportDescription));}}
    public string ExportDescription=>ExportDocument is null?"选择并打开导出记录。":ExportDocument.Stale?"原业务已变化：只可下载历史快照，不应当作当前数据记账。":$"SHA-256：{ExportDocument.Summary.Hash}\n可重复下载同一快照；外部接收、记账与冲销均未确认。";
    private FinanceOrderDetails? _details;public FinanceOrderDetails? Details{get=>_details;set{Set(ref _details,value);Changed(nameof(ProblemsText));}}
    public string ProblemsText=>Details is null?"选择订单并打开明细。":Details.Order.Problems.Count==0?"本地账单与业务金额对平；来源仍未经过平台自动核验。":string.Join("\n",Details.Order.Problems.Select(s=>"• "+s));
    private FinanceReport? _report;public FinanceReport? Report{get=>_report;set{Set(ref _report,value);Changed(nameof(ReportText));}}
    public string ReportText=>Report is null?"":$"累计 {Report.Orders} 张订单 · {Report.Incomplete} 张待完善 · {Report.UnmatchedRows} 笔待匹配；按已导入账单统计，不代表平台最终结算。";
    public string RowPageText=>$"第 {_rowPage} 页 / 共 {_rowTotal} 笔";public string OrderPageText=>$"第 {_orderPage} 页 / 共 {_orderTotal} 单";public string PendingText=>$"恢复未确认操作（{api.PendingCount}）";
    private static string Number(string prefix)=>prefix+"-"+Guid.NewGuid().ToString("N");
    private bool HasReason=>!string.IsNullOrWhiteSpace(Actor)&&!string.IsNullOrWhiteSpace(Reason);
    private ICommand Command(Func<Task> action,Func<bool>? can=null,string? permission=null)=>new AsyncCommand(async()=>{IsBusy=true;try{await action();}finally{IsBusy=false;Changed(nameof(PendingText));}},e=>Message="操作未完成："+e.Message,()=>!IsBusy&&(permission is null||api.Allows(permission))&&(can?.Invoke()??true));
    public ICommand RefreshCommand=>Command(async()=>{_rowPage=_orderPage=1;await RefreshAsync();});
    public ICommand PreviewCommand=>Command(PreviewAsync,()=>!string.IsNullOrWhiteSpace(Content), permission:"finance.write");
    public ICommand ImportCommand=>Command(ImportAsync,()=>HasReason&&Preview?.Valid==true, permission:"finance.write");
    public ICommand FileCommand=>Command(async()=>{var d=new OpenFileDialog{Filter="UTF-8 CSV|*.csv"};if(d.ShowDialog()==true){if(new FileInfo(d.FileName).Length>120000)throw new BusinessRuleException("文件超过 120 KB，请拆分批次。");Content=await File.ReadAllTextAsync(d.FileName,new UTF8Encoding(false,true));FileName=Path.GetFileName(d.FileName);Preview=null;}});
    public ICommand SampleCommand=>Command(()=>{LoadSample();return Task.CompletedTask;});
    public ICommand VoidCommand=>Command(VoidAsync,()=>HasReason&&SelectedBatch?.Source.State=="Active", permission:"finance.write");
    public ICommand OriginalCommand=>Command(async()=>{if(SelectedBatch is null)return;var raw=await api.GetAsync<JsonElement>($"/api/finance/batches/{SelectedBatch.Source.Id}/original");Content=raw.GetProperty("content").GetString()??"";Preview=null;Tab=0;Message="已载入该批次原文；请核对平台和映射，不要误作新账单提交。";});
    public ICommand RematchCommand=>Command(async()=>{await Post("/api/finance/rematch",new FinanceAction("",0,Actor,Reason));await RefreshAsync();},()=>HasReason, permission:"finance.write");
    public ICommand OpenRowCommand=>Command(async()=>{if(SelectedRow is not null)BillDetails=await api.GetAsync<BillDetails>("/api/finance/rows/"+SelectedRow.Source.Id);},()=>SelectedRow is not null);
    public ICommand SearchMatchCommand=>Command(FindMatchOrdersAsync);
    public ICommand LoadCasesCommand=>Command(LoadMatchCasesAsync,()=>MatchOrder is not null);
    public ICommand MatchCommand=>Command(MatchAsync,()=>HasReason&&BillDetails is not null&&MatchOrder is not null, permission:"finance.write");
    public ICommand RowPreviousCommand=>Command(async()=>{_rowPage--;await LoadRowsAsync();},()=>_rowPage>1);public ICommand RowNextCommand=>Command(async()=>{_rowPage++;await LoadRowsAsync();},()=>_rowPage*20<_rowTotal);
    public ICommand OrderPreviousCommand=>Command(async()=>{_orderPage--;await LoadOrdersAsync();},()=>_orderPage>1);public ICommand OrderNextCommand=>Command(async()=>{_orderPage++;await LoadOrdersAsync();},()=>_orderPage*20<_orderTotal);
    public ICommand OpenOrderCommand=>Command(async()=>{if(SelectedOrder is not null)await OpenOrderAsync(SelectedOrder.Source.OrderId);},()=>SelectedOrder is not null);
    public ICommand FeesCommand=>Command(FeesAsync,()=>HasReason&&Details is not null, permission:"finance.write");
    public ICommand CostCommand=> new ScopedCommand(Command(CostAsync,()=>HasReason&&SelectedSku is not null, permission:"finance.write"),()=>api.HasAllShops);
    public ICommand FillCostCommand=>Command(FillCostAsync,()=>HasReason&&SelectedCost?.UnitCost is null&&SelectedCost is not null, permission:"finance.write");
    public ICommand CreateExportCommand=>Command(CreateExportAsync,()=>HasReason&&Details?.Order.Profit is not null, permission:"finance.write");
    public ICommand OpenExportCommand=>Command(OpenExportAsync,()=>SelectedExport is not null);
    public ICommand SaveExportCommand=>Command(async()=>{var dialog=new SaveFileDialog{Filter="财务交接 JSON|*.json",FileName=ExportDocument?.Summary.Number+".json"};if(dialog.ShowDialog()==true)await SaveExportAsync(dialog.FileName);},()=>HasReason&&ExportDocument is not null, permission:"finance.write");
    public ICommand AuditCommand=> new ScopedCommand(Command(async()=>{var a=await api.GetAsync<FinanceAudit>("/api/finance/audit");Message=a.Passed?"结构检查通过。业务金额差异请在订单对账中处理。":string.Join("；",a.Problems);}),()=>api.HasAllShops);
    public ICommand RecoverCommand=>Command(async()=>{await api.RetryPendingAsync();await RefreshAsync();Message="原请求已重放，请核对最新状态。";},()=>api.PendingCount>0);
    private async Task<FinanceResult> Post(string path,object request){try{var result=await api.PostCommandAsync<FinanceResult>(path,request);Message=result.Message;return result;}finally{Changed(nameof(PendingText));}}
    public BillInput Input()
    {
        var types=new Dictionary<string,BillKind>(StringComparer.Ordinal);
        foreach(var group in new[]{(SaleTypes,BillKind.Sale),(RefundTypes,BillKind.Refund),(FeeTypes,BillKind.PlatformFee),(FreightTypes,BillKind.Freight)})foreach(var alias in group.Item1.Split('|',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries))if(!types.TryAdd(alias,group.Item2))throw new BusinessRuleException("类型值不能重复映射。");
        if(SelectedShop is null)throw new BusinessRuleException("请先登记并选择账单所属店铺。");
        return new(Platform.Id,ShopId,BatchNumber,FileName,Content,new(Columns[0].Value,Columns[1].Value,Columns[2].Value,Columns[3].Value,Columns[4].Value,Columns[5].Value,Columns[6].Value),types);
    }
    public void LoadSample(){Content=$"流水号,订单号,类型,金额,发生时间,币种,售后编号\r\nSAMPLE-001,示例订单请替换,收入,10.00,{DateTimeOffset.Now:O},CNY,\r\n";FileName="格式示例.csv";Preview=null;Message="这是格式示例。请替换为可核对的账单数据；未知订单会进入待匹配。";}
    public async Task PreviewAsync(){var input=Input();Preview=await api.PostAsync<BillPreview>("/api/finance/preview",input);_previewInput=JsonSerializer.Serialize(input,ErpJson.Options);Message=PreviewText;}
    public async Task ImportAsync(){var input=Input();if(Preview?.Valid!=true||JsonSerializer.Serialize(input,ErpJson.Options)!=_previewInput)throw new BusinessRuleException("账单或映射已变化，请重新预览。");var r=await Post("/api/finance/batches",new ImportBillRequest("",input,Actor,Reason));Preview=null;await RefreshAsync();SelectedBatch=Batches.Single(b=>b.Source.Id==r.Id);Tab=1;}
    public async Task VoidAsync(){if(SelectedBatch is null)return;await Post($"/api/finance/batches/{SelectedBatch.Source.Id}/void",new FinanceAction("",SelectedBatch.Source.Version,Actor,Reason));await RefreshAsync();}
    private static void Replace<T>(ObservableCollection<T> target,IEnumerable<T> values){target.Clear();foreach(var value in values)target.Add(value);}
    public async Task RefreshAsync()
    {
        var workspace=await api.GetAsync<WorkspaceProfile>("/api/workspace");var selectedPlatform=Platform.Id;_shopOptions=workspace.Shops;Platforms.Clear();foreach(var p in SeedData.Platforms.Where(p=>_shopOptions.Any(s=>s.Platform==p.Id)))Platforms.Add(p);Platform=Platforms.FirstOrDefault(p=>p.Id==selectedPlatform)??Platforms.FirstOrDefault()??SeedData.Platforms[0];RefreshShops();

        var detailId=Details?.Order.OrderId;
        Replace(Batches,(await api.GetAsync<List<BillBatch>>("/api/finance/batches")).Select(b=>new FinanceBatchRow(b)));SelectedBatch=Batches.FirstOrDefault();
        await LoadRowsAsync();await LoadOrdersAsync();await LoadCostsAsync();
        Replace(Exports,(await api.GetAsync<List<FinanceExport>>("/api/finance/exports")).Select(e=>new FinanceExportRow(e)));SelectedExport=Exports.FirstOrDefault();
        Report=await api.GetAsync<FinanceReport>("/api/finance/report");if(detailId is not null)await OpenOrderAsync(detailId);
    }
    private async Task LoadRowsAsync(){var page=await api.GetAsync<BillPage>($"/api/finance/rows?page={_rowPage}&search={Uri.EscapeDataString(Search)}&unmatched={Unmatched.ToString().ToLowerInvariant()}");Replace(Rows,page.Items.Select(r=>new FinanceBillRow(r)));_rowTotal=page.Total;Changed(nameof(RowPageText));BillDetails=null;}
    private async Task LoadOrdersAsync(){var page=await api.GetAsync<FinanceOrderPage>($"/api/finance/orders?page={_orderPage}&search={Uri.EscapeDataString(OrderSearch)}&differences={Differences.ToString().ToLowerInvariant()}");Replace(Orders,page.Items.Select(r=>new FinanceOrderRow(r)));_orderTotal=page.Total;Changed(nameof(OrderPageText));}
    private async Task LoadCostsAsync(){Replace(SkuCosts,await api.GetAsync<List<SkuCost>>("/api/finance/sku-costs"));Replace(Costs,await api.GetAsync<List<CostSnapshot>>("/api/finance/costs"));SelectedSku=SkuCosts.FirstOrDefault();SelectedCost=null;}
    public async Task FindMatchOrdersAsync(){var page=await api.GetAsync<FinanceOrderPage>("/api/finance/orders?pageSize=100&search="+Uri.EscapeDataString(MatchSearch));Replace(MatchOrders,page.Items.Select(o=>new FinanceOrderRow(o)));MatchOrder=MatchOrders.FirstOrDefault();}
    public async Task LoadMatchCasesAsync(){if(MatchOrder is null)return;var page=await api.GetAsync<AfterSalePage>("/api/after-sales/cases?pageSize=100&search="+Uri.EscapeDataString(MatchOrder.Source.ExternalOrderId));Replace(MatchCases,page.Items.Where(c=>c.OrderId==MatchOrder.Source.OrderId&&c.State is AfterSaleState.Approved or AfterSaleState.Closed));MatchCase=MatchCases.FirstOrDefault();}
    public async Task MatchAsync(){if(BillDetails is null||MatchOrder is null)return;await Post($"/api/finance/rows/{BillDetails.Row.Id}/match",new MatchBillRequest("",BillDetails.Row.Version,MatchOrder.Source.OrderId,BillDetails.Row.Kind==BillKind.Refund?MatchCase?.Id:null,MatchEvidence,Actor,Reason));await RefreshAsync();}
    public async Task OpenOrderAsync(string id){Details=await api.GetAsync<FinanceOrderDetails>("/api/finance/orders/"+id);ExpectedFee=Details.FeePlan.PlatformFee?.ToString(CultureInfo.InvariantCulture)??"";ExpectedFreight=Details.FeePlan.Freight?.ToString(CultureInfo.InvariantCulture)??"";FeeEvidence=Details.FeePlan.Evidence;}
    private static decimal Amount(string value){if(!decimal.TryParse(value,NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out var result))throw new BusinessRuleException("请填写有效的非负元金额，不使用千分符。");return result;}
    public async Task FeesAsync(){if(Details is null)return;var id=Details.Order.OrderId;await Post($"/api/finance/orders/{id}/fees",new SetFeePlanRequest("",Details.FeePlan.Version,Amount(ExpectedFee),Amount(ExpectedFreight),FeeEvidence,Actor,Reason));await RefreshAsync();await OpenOrderAsync(id);}
    public async Task CostAsync(){if(SelectedSku is null)return;await Post("/api/finance/sku-costs/"+SelectedSku.SkuId,new SetSkuCostRequest("",SelectedSku.Version,Amount(CostValue),CostEvidence,Actor,Reason));await LoadCostsAsync();}
    public async Task FillCostAsync(){if(SelectedCost is null)return;await Post($"/api/finance/costs/{SelectedCost.ShipmentLineId}/{SelectedCost.SkuId}",new FillCostRequest("",SelectedCost.Version,Amount(HistoricalCost),HistoricalEvidence,Actor,Reason));await RefreshAsync();}
    public async Task CreateExportAsync(){if(Details is null)return;var r=await Post("/api/finance/exports",new CreateFinanceExportRequest("",ExportNumber,Target,[Details.Order.OrderId],Actor,Reason));await RefreshAsync();SelectedExport=Exports.Single(e=>e.Source.Id==r.Id);await OpenExportAsync();Tab=4;}
    public async Task OpenExportAsync(){if(SelectedExport is not null)ExportDocument=await api.GetAsync<FinanceExportDocument>("/api/finance/exports/"+SelectedExport.Source.Id);}
    public async Task SaveExportAsync(string path)
    {
        if(ExportDocument is null)return;var d=ExportDocument;
        path=Path.GetFullPath(path);if(!path.EndsWith(".json",StringComparison.OrdinalIgnoreCase))throw new BusinessRuleException("请选择 .json 文件名，汇总另存为同名 CSV。");
        var csvPath=Path.ChangeExtension(path,".csv");
        if(File.Exists(csvPath)&&await File.ReadAllTextAsync(csvPath)!=d.Csv)throw new BusinessRuleException("同名 CSV 已存在且内容不同，请使用新的文件名。");
        await File.WriteAllTextAsync(path,d.Json,new UTF8Encoding(false));await File.WriteAllTextAsync(csvPath,d.Csv,new UTF8Encoding(true));
        if(!d.Stale){await Post($"/api/finance/exports/{d.Summary.Id}/saved",new FinanceAction("",d.Summary.Version,Actor,Reason));await RefreshAsync();SelectedExport=Exports.Single(e=>e.Source.Id==d.Summary.Id);await OpenExportAsync();}
        Message=d.Stale?"历史快照已保存，原业务已有变化；未登记为当前导出。":"JSON 明细与 CSV 汇总已保存；未发送到财务系统，未确认记账。";
    }
}
