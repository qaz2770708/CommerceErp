using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Windows.Input;
using CommerceErp.Core;

namespace CommerceErp.Desktop;

public sealed record OrderRow(OrderSummary Source)
{
    public string Id => Source.Id;
    public string ExternalOrderId => Source.ExternalOrderId;
    public string PlatformName => SeedData.Platforms.Single(p => p.Id == Source.Platform).Name;
    public string StateText => OperationsViewModel.StateName(Source.State);
    public string ShippingText => OperationsViewModel.ShippingName(Source.ShippingState);
    public decimal PaidAmount => Source.PaidAmount;
    public string IssueSummary => Source.IssueSummary;
}
public sealed record StateChoice(string Name, ReviewState? State);
public sealed partial class OperationsViewModel(LocalApiClient api) : ObservableObject
{
    public ICommand InventoryDocumentsCommand=>Command(async()=>{var window=new InventoryDocumentWindow(api){Owner=System.Windows.Application.Current.MainWindow};await window.RefreshAsync();window.ShowDialog();await RefreshAsync();},permission:"stockdocs.read");
    public ICommand ManualOrderCommand=>Command(async()=>{var window=new ManualOrderWindow(api){Owner=System.Windows.Application.Current.MainWindow};await window.LoadAsync();if(window.ShowDialog()==true){InputJson=JsonSerializer.Serialize(window.Order,ErpJson.Options);await ImportAsync();}},permission:"orders.write");
    public ObservableCollection<OrderRow> Orders { get; } = [];
    public ObservableCollection<InventoryRow> Inventory { get; } = [];
    public ObservableCollection<StockLedgerRow> Ledger { get; } = [];
    public ObservableCollection<SampleDescriptor> Samples { get; } = [];
    public List<StateChoice> States { get; } = [new("全部状态", null), .. Enum.GetValues<ReviewState>().Select(s => new StateChoice(StateName(s), s))];
    public static string StateName(ReviewState s) => s switch { ReviewState.PendingReview => "待审核", ReviewState.Approved => "已审核", ReviewState.Exception => "异常", ReviewState.OnHold => "挂起", ReviewState.Cancelled => "已取消", _ => s.ToString() };
    public static string ShippingName(string state) => state switch { "Partial" => "部分出库", "Complete" => "全部出库", _ => "未出库" };
    private bool _busy;
    public bool IsBusy { get => _busy; set { Set(ref _busy, value); Changed(nameof(IsReady)); CommandManager.InvalidateRequerySuggested(); } }
    public bool IsReady => !IsBusy;
    private string _message = "订单接入会实际写入本地数据库并预占本地模拟库存。";
    public string Message { get => _message; set => Set(ref _message, value); }
    private string _search = "";
    public string Search { get => _search; set => Set(ref _search, value); }
    private StateChoice? _state;
    public StateChoice? SelectedState { get => _state; set => Set(ref _state, value); }
    private int _page = 1, _total;
    public string PageText => $"第 {_page} 页 / 共 {_total} 单";
    private BusinessDashboard? _dashboard;
    public BusinessDashboard? Dashboard { get => _dashboard; set => Set(ref _dashboard, value); }
    private OrderRow? _selected;
    public OrderRow? SelectedOrder { get => _selected; set { if (Set(ref _selected, value)) { Details = null; DetailJson = "选中订单后点击‘打开详情’。"; } } }
    private OrderDetails? _details;
    public OrderDetails? Details { get => _details; set { Set(ref _details, value); Changed(nameof(DetailTitle)); Changed(nameof(DetailState)); Changed(nameof(CanCorrect)); CommandManager.InvalidateRequerySuggested(); } }
    public string DetailTitle => Details?.Summary.ExternalOrderId ?? "订单详情";
    public string DetailState => Details is null ? "尚未打开订单" : $"{StateName(Details.Summary.State)} · {ShippingName(Details.Summary.ShippingState)} · 售后处理中 {Details.Summary.ActiveAfterSales} · v{Details.Summary.Version}";
    public bool CanCorrect => Details is not null && Details.Summary.State is not (ReviewState.Approved or ReviewState.Cancelled);
    private string _detailJson = "选中订单后点击‘打开详情’。";
    public string DetailJson { get => _detailJson; set => Set(ref _detailJson, value); }
    private string _correctionJson = "";
    public string CorrectionJson { get => _correctionJson; set => Set(ref _correctionJson, value); }
    private string _reason = "";
    public string Reason { get => _reason; set => Set(ref _reason, value); }
    private SampleDescriptor? _sample;
    public SampleDescriptor? SelectedSample { get => _sample; set => Set(ref _sample, value); }
    private string _inputJson = "";
    public string InputJson { get => _inputJson; set => Set(ref _inputJson, value); }
    private bool _autoReview = true;
    public bool AutoReview { get => _autoReview; set => Set(ref _autoReview, value); }
    private InventoryRow? _stock;
    public InventoryRow? SelectedStock
    {
        get => _stock;
        set { if (Set(ref _stock, value) && value is not null) { Frozen = value.Frozen.ToString(CultureInfo.InvariantCulture); Safety = value.Safety.ToString(CultureInfo.InvariantCulture); } }
    }
    private string _receiveQuantity = "10", _frozen = "0", _safety = "0", _stockReason = "";
    public string ReceiveQuantity { get => _receiveQuantity; set => Set(ref _receiveQuantity, value); }
    public string Frozen { get => _frozen; set => Set(ref _frozen, value); }
    public string Safety { get => _safety; set => Set(ref _safety, value); }
    public string StockReason { get => _stockReason; set => Set(ref _stockReason, value); }
    public string PendingText => $"重试未完成操作（{api.PendingCount}）";
    private int _editorTab = 1;
    public int EditorTab { get => _editorTab; set => Set(ref _editorTab, value); }
    public ICommand RefreshCommand => Command(async () => { _page = 1; await RefreshAsync(); });
    public ICommand PreviousCommand => Command(async () => { _page--; await LoadOrdersAsync(); }, () => _page > 1);
    public ICommand NextCommand => Command(async () => { _page++; await LoadOrdersAsync(); }, () => _page * 20 < _total);
    public ICommand OpenCommand => Command(OpenAsync, () => SelectedOrder is not null);
    public ICommand NewSampleCommand => Command(NewSampleAsync, () => SelectedSample is not null);
    public ICommand ImportCommand => Command(ImportAsync, () => !string.IsNullOrWhiteSpace(InputJson), permission:"orders.write");
    public ICommand ReviewCommand => Command(ReviewAsync, () => Details is not null && Details.Summary.State != ReviewState.Cancelled, permission:"orders.write");
    public ICommand CancelCommand => Command(CancelAsync, () => Details is not null && Details.Summary.State != ReviewState.Cancelled && Details.Summary.ShippingState == "Unshipped" && Details.Summary.ActiveAfterSales == 0 && !string.IsNullOrWhiteSpace(Reason), permission:"orders.write");
    public ICommand CorrectCommand => Command(CorrectAsync, () => CanCorrect && !string.IsNullOrWhiteSpace(Reason), permission:"orders.write");
    public ICommand ReceiveCommand => Command(ReceiveAsync, () => SelectedStock is not null && !string.IsNullOrWhiteSpace(StockReason), permission:"inventory.write");
    public ICommand PolicyCommand => Command(PolicyAsync, () => SelectedStock is not null && !string.IsNullOrWhiteSpace(StockReason), permission:"inventory.write");
    public ICommand ConsistencyCommand => new ScopedCommand(Command(CheckAsync, permission:"inventory.read"),()=>api.HasAllShops);
    public ICommand RetryCommand => Command(RetryAsync, () => api.PendingCount > 0);
    private ICommand Command(Func<Task> action, Func<bool>? enabled = null, string? permission = null) => new AsyncCommand(() => BusyAsync(action), ShowError, () => !IsBusy && (permission is null || api.Allows(permission)) && (enabled?.Invoke() ?? true));
    private async Task BusyAsync(Func<Task> action)
    {
        IsBusy = true;
        try { await action(); }
        finally { IsBusy = false; Changed(nameof(PendingText)); }
    }
    public void ShowError(Exception e) { Message = "操作未完成：" + e.Message + (api.PendingCount > 0 ? " 未确认的请求已保留，可用原请求重试。" : ""); Changed(nameof(PendingText)); }
    public async Task InitializeAsync(IEnumerable<SampleDescriptor> samples)
    {
        Samples.Clear();foreach (var sample in samples) Samples.Add(sample); SelectedSample = Samples.FirstOrDefault(); SelectedState = States.First();
        await NewSampleAsync(); await RefreshAsync();
        if (api.PendingCount > 0) Message = $"发现 {api.PendingCount} 个结果未确认的操作，请点击‘重试未完成操作’核实结果。";
    }
    public async Task RefreshAsync()
    {
        Dashboard = await api.GetAsync<BusinessDashboard>("/api/business/dashboard"); await LoadOrdersAsync();
        if(api.Allows("inventory.read"))
        {
        var sku = SelectedStock?.SkuId; var rows = await api.GetAsync<List<InventoryRow>>("/api/inventory");
        Inventory.Clear(); foreach (var row in rows) Inventory.Add(row); SelectedStock = Inventory.FirstOrDefault(i => i.SkuId == sku) ?? Inventory.FirstOrDefault();
        Ledger.Clear(); foreach (var row in await api.GetAsync<List<StockLedgerRow>>("/api/inventory/ledger")) Ledger.Add(row);
        }
        Changed(nameof(PendingText));
    }
    private async Task LoadOrdersAsync()
    {
        var state = SelectedState?.State is { } s ? "&state=" + s : "";
        var page = await api.GetAsync<OrderPage>($"/api/orders?page={_page}&pageSize=20&search={Uri.EscapeDataString(Search)}{state}");
        Orders.Clear(); foreach (var row in page.Items) Orders.Add(new(row)); _total = page.Total; Changed(nameof(PageText));
    }
    public async Task OpenAsync() { if (SelectedOrder is not null) await LoadDetailsAsync(SelectedOrder.Id); }
    private async Task LoadDetailsAsync(string id)
    {
        Details = await api.GetAsync<OrderDetails>("/api/orders/" + id);
        DetailJson = JsonSerializer.Serialize(new { Details.Normalized, Details.Issues, Details.Reservations, Details.History }, ErpJson.Options);
        CorrectionJson = JsonSerializer.Serialize(Details.Source, ErpJson.Options);
        EditorTab = 0;
    }
    public async Task NewSampleAsync()
    {
        if (SelectedSample is null) return;
        var sample = await api.GetAsync<SampleOrder>("/api/samples/" + SelectedSample.Id);
        InputJson = JsonSerializer.Serialize(sample with { ExternalOrderId = "DEMO-" + Guid.NewGuid().ToString("N") }, ErpJson.Options);
        EditorTab = 1;
        Message = "已生成新模拟单号；再次接入同一份 JSON 可验证业务去重。";
    }
    private static SampleOrder Parse(string json) => JsonSerializer.Deserialize<SampleOrder>(json, ErpJson.Options) ?? throw new BusinessRuleException("订单 JSON 不能为空。");
    public async Task ImportAsync()
    {
        var r = await api.PostCommandAsync<OrderCommandResult>("/api/orders", new { order = Parse(InputJson), autoReview = AutoReview });
        await RefreshAsync(); await LoadDetailsAsync(r.OrderId); Message = r.Message;
    }
    public async Task ReviewAsync()
    {
        if (Details is null) return; var id = Details.Summary.Id;
        var r = await api.PostCommandAsync<OrderCommandResult>($"/api/orders/{id}/review", new { expectedVersion = Details.Summary.Version });
        await RefreshAsync(); await LoadDetailsAsync(id); Message = r.Message;
    }
    public async Task CancelAsync()
    {
        if (Details is null) return; var id = Details.Summary.Id;
        var r = await api.PostCommandAsync<OrderCommandResult>($"/api/orders/{id}/cancel", new { expectedVersion = Details.Summary.Version, reason = Reason });
        await RefreshAsync(); await LoadDetailsAsync(id); Message = r.Message;
    }
    public async Task CorrectAsync()
    {
        if (Details is null) return; var id = Details.Summary.Id;
        var r = await api.PostCommandAsync<OrderCommandResult>($"/api/orders/{id}/correct", new { expectedVersion = Details.Summary.Version, order = Parse(CorrectionJson), reason = Reason });
        await RefreshAsync(); await LoadDetailsAsync(id); Message = r.Message;
    }
    private static decimal Number(string input) => decimal.TryParse(input, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) ? value : throw new BusinessRuleException("请输入非负数，最多三位小数，使用小数点。");
    public async Task ReceiveAsync()
    {
        if (SelectedStock is null) return;
        var r = await api.PostCommandAsync<StockCommandResult>($"/api/inventory/{SelectedStock.SkuId}/receive", new { quantity = Number(ReceiveQuantity), reason = StockReason });
        await RefreshAsync(); Message = r.Message;
    }
    public async Task PolicyAsync()
    {
        if (SelectedStock is null) return;
        var r = await api.PostCommandAsync<StockCommandResult>($"/api/inventory/{SelectedStock.SkuId}/policy", new { expectedVersion = SelectedStock.Version, frozen = Number(Frozen), safety = Number(Safety), reason = StockReason });
        await RefreshAsync(); Message = r.Message;
    }
    public async Task CheckAsync()
    {
        var report = await api.GetAsync<ConsistencyReport>("/api/business/consistency");
        if (!report.Passed) throw new BusinessRuleException(string.Join("；", report.Problems));
        Message = $"一致性检查通过：{report.CheckedOrders} 个订单、{report.CheckedBalances} 个库存余额、{report.ActiveReservations} 条有效预占与流水相符。";
    }
    public async Task RetryAsync() { var count = await api.RetryPendingAsync(); await RefreshAsync(); Message = $"已确认 {count} 个未完成操作的结果；请重新打开订单详情查看当前状态。"; }
}
