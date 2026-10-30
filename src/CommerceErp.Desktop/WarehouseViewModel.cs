using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Windows.Input;
using CommerceErp.Core;
using Microsoft.Win32;

namespace CommerceErp.Desktop;

public sealed class QuantityRow(AllocatableLine source) : ObservableObject
{
    public AllocatableLine Source { get; } = source;
    private string _quantity = source.Remaining.ToString(CultureInfo.InvariantCulture);
    public string Quantity { get => _quantity; set => Set(ref _quantity, value); }
}
public sealed record CartRow(string OrderId, string ExternalOrderId, string LineId, string SkuCode, decimal Quantity, long Version);
public sealed class ShipmentRow(ShipmentSummary source) : ObservableObject
{
    public ShipmentSummary Source { get; } = source;
    private bool _selected;
    public bool Selected { get => _selected; set => Set(ref _selected, value); }
}
public sealed class SplitRow(ShipmentLineView source) : ObservableObject
{
    public ShipmentLineView Source { get; } = source;
    private string _quantity = "0";
    public string Quantity { get => _quantity; set => Set(ref _quantity, value); }
}
public sealed class WarehouseViewModel(LocalApiClient api) : ObservableObject
{
    public ObservableCollection<ShippingCandidate> Candidates { get; } = [];
    public ObservableCollection<QuantityRow> SourceLines { get; } = [];
    public ObservableCollection<CartRow> Cart { get; } = [];
    public ObservableCollection<ShipmentRow> Shipments { get; } = [];
    public ObservableCollection<SplitRow> SplitLines { get; } = [];
    public ObservableCollection<DeliveryJobView> Jobs { get; } = [];
    public ObservableCollection<BarcodeRow> Barcodes { get; } = [];
    public ObservableCollection<InventoryRow> Skus { get; } = [];
    public List<string> CarrierOptions { get; } = ["顺丰", "中通", "圆通", "申通", "韵达", "极兔", "京东物流", "邮政EMS", "其他"];
    private bool _busy;
    public bool IsBusy { get => _busy; set { Set(ref _busy, value); Changed(nameof(IsReady)); CommandManager.InvalidateRequerySuggested(); } }
    public bool IsReady => !IsBusy;
    private int _tab, _workTab, _page = 1, _total;
    public int Tab { get => _tab; set => Set(ref _tab, value); }
    public int WorkTab { get => _workTab; set => Set(ref _workTab, value); }
    public string PageText => $"第 {_page} 页 · 共 {_total} 张出库单";
    private string _actor = Environment.UserName, _reason = "", _search = "", _message = "选择已审核订单，补齐收货资料后配单。仓储操作会实际写入本地库存账。";
    public string Actor { get => _actor; set => Set(ref _actor, value); }
    public string Reason { get => _reason; set => Set(ref _reason, value); }
    public string Search { get => _search; set => Set(ref _search, value); }
    public string Message { get => _message; set => Set(ref _message, value); }
    public string PendingText => $"恢复未确认操作（{api.PendingCount}）";
    private ShippingCandidate? _selectedCandidate;
    public ShippingCandidate? SelectedCandidate { get => _selectedCandidate; set { Set(ref _selectedCandidate, value); CommandManager.InvalidateRequerySuggested(); } }
    private OrderShippingPreparation? _preparation;
    public OrderShippingPreparation? Preparation { get => _preparation; set => Set(ref _preparation, value); }
    private string _recipient = "", _phone = "", _province = "", _city = "", _district = "", _street = "", _postal = "";
    public string Recipient { get => _recipient; set => Set(ref _recipient, value); }
    public string Phone { get => _phone; set => Set(ref _phone, value); }
    public string Province { get => _province; set => Set(ref _province, value); }
    public string City { get => _city; set => Set(ref _city, value); }
    public string District { get => _district; set => Set(ref _district, value); }
    public string Street { get => _street; set => Set(ref _street, value); }
    public string PostalCode { get => _postal; set => Set(ref _postal, value); }
    private bool _allowMerge;
    public bool AllowMerge { get => _allowMerge; set => Set(ref _allowMerge, value); }
    private ShipmentRow? _selectedShipment;
    public ShipmentRow? SelectedShipment
    {
        get => _selectedShipment;
        set { Set(ref _selectedShipment, value); if (value is not null && Details?.Summary.Id != value.Source.Id) { Details = null; SplitLines.Clear(); } }
    }
    private ShipmentDetails? _details;
    public ShipmentDetails? Details
    {
        get => _details;
        set { Set(ref _details, value); foreach (var p in new[] { nameof(HasShipment), nameof(IsDraft), nameof(IsPicking), nameof(IsPacked), nameof(CanVoid), nameof(AddressText) }) Changed(p); CommandManager.InvalidateRequerySuggested(); }
    }
    public bool HasShipment => Details is not null;
    public bool IsDraft => Details?.Summary.State == ShipmentState.Draft;
    public bool IsPicking => Details?.Summary.State == ShipmentState.Picking;
    public bool IsPacked => Details?.Summary.State == ShipmentState.Packed;
    public bool CanVoid => IsDraft || IsPicking || IsPacked;
    public string AddressText => Details is null ? "打开出库单查看作业信息" : $"{Details.Address.Recipient} · {Details.Address.Phone}\n{Details.Address.Province} {Details.Address.City} {Details.Address.District} {Details.Address.Street}";
    private string _scanCode = "", _scanQuantity = "1", _carrier = "顺丰", _tracking = "", _newBarcode = "";
    public string ScanCode { get => _scanCode; set => Set(ref _scanCode, value); }
    public string ScanQuantity { get => _scanQuantity; set => Set(ref _scanQuantity, value); }
    public string Carrier { get => _carrier; set => Set(ref _carrier, value); }
    public string Tracking { get => _tracking; set => Set(ref _tracking, value); }
    public string NewBarcode { get => _newBarcode; set => Set(ref _newBarcode, value); }
    private InventoryRow? _barcodeSku;
    public InventoryRow? BarcodeSku { get => _barcodeSku; set => Set(ref _barcodeSku, value); }
    private DeliveryJobView? _selectedJob;
    public DeliveryJobView? SelectedJob { get => _selectedJob; set => Set(ref _selectedJob, value); }
    private CartRow? _selectedCart;
    public CartRow? SelectedCart { get => _selectedCart; set => Set(ref _selectedCart, value); }
    public ICommand RefreshCommand => Command(async () => { _page = 1; await RefreshAsync(); });
    public ICommand PreviousCommand => Command(async () => { _page--; await RefreshAsync(); }, () => _page > 1);
    public ICommand NextCommand => Command(async () => { _page++; await RefreshAsync(); }, () => _page * 20 < _total);
    public ICommand OpenSourceCommand => Command(OpenSourceAsync, () => SelectedCandidate is not null);
    public ICommand SaveAddressCommand => Command(SaveAddressAsync, () => Preparation is not null && SelectedCandidate?.OrderId == Preparation.Order.OrderId && HasReason, permission:"warehouse.write");
    public ICommand AddCartCommand => Command(AddCartAsync, () => Preparation?.Profile is not null && SelectedCandidate?.OrderId == Preparation.Order.OrderId);
    public ICommand RemoveCartCommand => Command(() => { if (SelectedCart is not null) Cart.Remove(SelectedCart); return Task.CompletedTask; }, () => SelectedCart is not null);
    public ICommand ClearCartCommand => Command(() => { Cart.Clear(); return Task.CompletedTask; }, () => Cart.Count > 0);
    public ICommand CreateCommand => Command(CreateAsync, () => Cart.Count > 0 && HasReason, permission:"warehouse.write");
    public ICommand OpenShipmentCommand => Command(async () => { if (SelectedShipment is not null) await OpenShipmentAsync(SelectedShipment.Source.Id); }, () => SelectedShipment is not null);
    private bool NoAfterSaleHold => Details is not null && !Details.Summary.AfterSaleHold;
    public ICommand PickCommand => Command(() => ActAsync("pick"), () => IsDraft && NoAfterSaleHold, permission:"warehouse.write");
    public ICommand ScanCommand => Command(ScanAsync, () => IsPicking && NoAfterSaleHold && !string.IsNullOrWhiteSpace(ScanCode), permission:"warehouse.write");
    public ICommand ResetCommand => Command(() => ActAsync("reset"), () => IsPicking && HasReason, permission:"warehouse.write");
    public ICommand PackCommand => Command(() => ActAsync("pack"), () => IsPicking && NoAfterSaleHold && Details!.Checks.All(c => c.Required == c.Verified), permission:"warehouse.write");
    public ICommand WaybillCommand => Command(WaybillAsync, () => CanVoid && NoAfterSaleHold && HasReason && !string.IsNullOrWhiteSpace(Tracking), permission:"warehouse.write");
    public ICommand DispatchCommand => Command(() => ActAsync("dispatch"), () => IsPacked && NoAfterSaleHold && HasReason && Details!.Summary.TrackingNumber.Length > 0, permission:"warehouse.write");
    public ICommand VoidCommand => Command(() => ActAsync("void"), () => CanVoid && HasReason, permission:"warehouse.write");
    public ICommand SplitCommand => Command(SplitAsync, () => IsDraft && NoAfterSaleHold && HasReason && Details!.Summary.TrackingNumber == "", permission:"warehouse.write");
    public ICommand MergeCommand => Command(MergeAsync, () => HasReason && Shipments.Count(s => s.Selected) >= 2, permission:"warehouse.write");
    public ICommand RetryDeliveryCommand => Command(RetryDeliveryAsync, () => HasReason && SelectedJob is not null && SelectedJob.State != "Confirmed", permission:"warehouse.write");
    public ICommand SaveBarcodeCommand => new ScopedCommand(Command(SaveBarcodeAsync, () => HasReason && BarcodeSku is not null && !string.IsNullOrWhiteSpace(NewBarcode), permission:"warehouse.write"),()=>api.HasAllShops);
    public ICommand AuditCommand => new ScopedCommand(Command(AuditAsync),()=>api.HasAllShops);
    public ICommand RecoverCommand => Command(async () => { var n = await api.RetryPendingAsync(); await RefreshAsync(); if (Details is not null) await OpenShipmentAsync(Details.Summary.Id); Message = $"已恢复 {n} 个未确认操作。"; }, () => api.PendingCount > 0);
    public ICommand ExportCommand => Command(ExportPickListAsync, () => Details is not null);
    private bool HasReason => !string.IsNullOrWhiteSpace(Reason);
    private ICommand Command(Func<Task> action, Func<bool>? enabled = null, string? permission = null) => new AsyncCommand(async () =>
    {
        IsBusy = true;
        try { await action(); }
        finally { IsBusy = false; Changed(nameof(PendingText)); }
    }, e => Message = "操作未完成：" + e.Message, () => !IsBusy && (permission is null || api.Allows(permission)) && !string.IsNullOrWhiteSpace(Actor) && (enabled?.Invoke() ?? true));
    public async Task RefreshAsync()
    {
        Candidates.Clear(); foreach (var item in await api.GetAsync<List<ShippingCandidate>>("/api/warehouse/orders?search=" + Uri.EscapeDataString(Search))) Candidates.Add(item);
        if (Preparation is not null)
        {
            if (Candidates.Any(c => c.OrderId == Preparation.Order.OrderId)) await LoadSourceAsync(Preparation.Order.OrderId);
            else { Preparation = null; SourceLines.Clear(); }
        }
        SelectedCandidate = Candidates.FirstOrDefault(c => c.OrderId == Preparation?.Order.OrderId) ?? Candidates.FirstOrDefault();
        var page = await api.GetAsync<ShipmentPage>($"/api/warehouse/shipments?page={_page}&pageSize=20&search={Uri.EscapeDataString(Search)}");
        Shipments.Clear(); foreach (var item in page.Items) Shipments.Add(new(item)); _total = page.Total; Changed(nameof(PageText));
        Jobs.Clear(); foreach (var item in await api.GetAsync<List<DeliveryJobView>>("/api/warehouse/deliveries")) Jobs.Add(item);
        Barcodes.Clear(); foreach (var item in await api.GetAsync<List<BarcodeRow>>("/api/warehouse/barcodes")) Barcodes.Add(item);
        var sku = BarcodeSku?.SkuId; Skus.Clear(); foreach (var item in await api.GetAsync<List<InventoryRow>>("/api/inventory")) Skus.Add(item); BarcodeSku = Skus.FirstOrDefault(s => s.SkuId == sku) ?? Skus.FirstOrDefault();
        Changed(nameof(PendingText));
    }
    public async Task OpenSourceAsync()
    {
        if (SelectedCandidate is null) return; await LoadSourceAsync(SelectedCandidate.OrderId);
    }
    private async Task LoadSourceAsync(string id)
    {
        Preparation = await api.GetAsync<OrderShippingPreparation>("/api/warehouse/orders/" + id);
        var a = Preparation.Profile?.Address; Recipient = a?.Recipient ?? ""; Phone = a?.Phone ?? ""; Province = a?.Province ?? ""; City = a?.City ?? ""; District = a?.District ?? ""; Street = a?.Street ?? ""; PostalCode = a?.PostalCode ?? ""; AllowMerge = Preparation.Profile?.AllowMerge ?? false;
        SourceLines.Clear(); foreach (var line in Preparation.Lines) SourceLines.Add(new(line));
    }
    public async Task SaveAddressAsync()
    {
        if (Preparation is null) return; var id = Preparation.Order.OrderId;
        var result = await api.PostCommandAsync<WarehouseResult>($"/api/warehouse/orders/{id}/address", new { expectedVersion = Preparation.Order.Version, address = new ShippingAddress(Recipient, Phone, Province, City, District, Street, PostalCode), allowMerge = AllowMerge, actor = Actor, reason = Reason });
        foreach (var line in Cart.Where(c => c.OrderId == id).ToList()) Cart.Remove(line);
        await LoadSourceAsync(id); await RefreshAsync(); Message = result.Message;
    }
    private static decimal Number(string text) => decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var n) && n >= 0 && decimal.Round(n, 3) == n ? n : throw new BusinessRuleException("数量须为非负数，最多三位小数。");
    public Task AddCartAsync()
    {
        if (Preparation is null) return Task.CompletedTask;
        var rows = SourceLines.Select(r => (r.Source, Qty: Number(r.Quantity))).Where(r => r.Qty > 0).ToList();
        if (rows.Count == 0 || rows.Any(r => r.Qty > r.Source.Remaining)) throw new BusinessRuleException("请选择有效配单数量，不能超过未分配数量。");
        foreach (var line in Cart.Where(c => c.OrderId == Preparation.Order.OrderId).ToList()) Cart.Remove(line);
        foreach (var row in rows) Cart.Add(new(Preparation.Order.OrderId, Preparation.Order.ExternalOrderId, row.Source.ExternalLineId, row.Source.SkuCode, row.Qty, Preparation.Order.Version));
        Message = "已加入待配单清单。可再选择其他订单加入，同单再次加入会替换该订单的清单数量。"; return Task.CompletedTask;
    }
    public async Task CreateAsync()
    {
        var result = await api.PostCommandAsync<WarehouseResult>("/api/warehouse/shipments", new { lines = Cart.Select(c => new AllocationLine(c.OrderId, c.LineId, c.Quantity, c.Version)).ToList(), actor = Actor, reason = Reason });
        Cart.Clear(); await RefreshAsync(); await OpenShipmentAsync(result.Id); Message = result.Message;
    }
    public async Task OpenShipmentAsync(string id)
    {
        Details = await api.GetAsync<ShipmentDetails>("/api/warehouse/shipments/" + id);
        SplitLines.Clear(); foreach (var line in Details.Lines) SplitLines.Add(new(line)); Carrier = Details.Summary.Carrier.Length > 0 ? Details.Summary.Carrier : "顺丰"; Tracking = Details.Summary.TrackingNumber;
        Tab = 1;
    }
    public async Task ActAsync(string action)
    {
        if (Details is null) return; var id = Details.Summary.Id;
        var result = await api.PostCommandAsync<WarehouseResult>($"/api/warehouse/shipments/{id}/{action}", new { expectedVersion = Details.Summary.Version, actor = Actor, reason = Reason });
        await RefreshAsync(); await OpenShipmentAsync(id); Message = result.Message;
    }
    public async Task ScanAsync()
    {
        if (Details is null) return; var id = Details.Summary.Id;
        var result = await api.PostCommandAsync<WarehouseResult>($"/api/warehouse/shipments/{id}/scan", new { expectedVersion = Details.Summary.Version, barcode = ScanCode.Trim(), quantity = Number(ScanQuantity), actor = Actor });
        await OpenShipmentAsync(id); ScanCode = ""; Message = result.Message;
    }
    public async Task WaybillAsync()
    {
        if (Details is null) return; var id = Details.Summary.Id;
        var result = await api.PostCommandAsync<WarehouseResult>($"/api/warehouse/shipments/{id}/waybill", new { expectedVersion = Details.Summary.Version, carrier = Carrier, trackingNumber = Tracking, actor = Actor, reason = Reason });
        await RefreshAsync(); await OpenShipmentAsync(id); Message = result.Message;
    }
    public async Task SplitAsync()
    {
        if (Details is null) return;
        var lines = SplitLines.Select(l => new SplitQuantity(l.Source.Id, Number(l.Quantity))).Where(l => l.Quantity > 0).ToList();
        var result = await api.PostCommandAsync<WarehouseResult>($"/api/warehouse/shipments/{Details.Summary.Id}/split", new { expectedVersion = Details.Summary.Version, lines, actor = Actor, reason = Reason });
        await RefreshAsync(); await OpenShipmentAsync(result.Id); WorkTab = 0; Message = result.Message;
    }
    public async Task MergeAsync()
    {
        var selected = Shipments.Where(s => s.Selected).Select(s => new ShipmentVersion(s.Source.Id, s.Source.Version)).ToList();
        var result = await api.PostCommandAsync<WarehouseResult>("/api/warehouse/merge", new { shipments = selected, actor = Actor, reason = Reason });
        await RefreshAsync(); await OpenShipmentAsync(result.Id); Message = result.Message;
    }
    public async Task RetryDeliveryAsync()
    {
        if (SelectedJob is null) return;
        var result = await api.PostCommandAsync<WarehouseResult>($"/api/warehouse/deliveries/{SelectedJob.Id}/retry", new { expectedVersion = SelectedJob.Version, actor = Actor, reason = Reason });
        await RefreshAsync(); Message = result.Message;
    }
    public async Task SaveBarcodeAsync()
    {
        if (BarcodeSku is null) return;
        var existing = Barcodes.SingleOrDefault(b => b.Barcode == NewBarcode.Trim());
        var result = await api.PostCommandAsync<WarehouseResult>("/api/warehouse/barcodes", new { expectedVersion = existing?.Version ?? 0, barcode = NewBarcode, skuId = BarcodeSku.SkuId, actor = Actor, reason = Reason });
        await RefreshAsync(); Message = result.Message;
    }
    public async Task AuditAsync()
    {
        var audit = await api.GetAsync<ConsistencyReport>("/api/business/consistency"); var warehouse = await api.GetAsync<WarehouseAudit>("/api/warehouse/audit");
        if (!audit.Passed || !warehouse.Passed) throw new BusinessRuleException(string.Join("；", audit.Problems.Concat(warehouse.Problems)));
        Message = $"检查通过：{warehouse.Shipments} 张出库单、{warehouse.Dispatched} 张已出库，订单分配、组件、流水与预占相符。";
    }
    private async Task ExportPickListAsync()
    {
        if (Details is null) return;
        var dialog = new SaveFileDialog { Filter = "可打印拣货单 HTML|*.html", FileName = Details.Summary.Number + "-picklist.html" };
        if (dialog.ShowDialog() == true) { await File.WriteAllTextAsync(dialog.FileName, BuildPickListHtml(), new UTF8Encoding(false)); Message = "拣货单已导出，可在浏览器中打开并打印；该文件包含收货资料，请按业务需要保管。"; }
    }
    public string BuildPickListHtml()
    {
        var d = Details ?? throw new BusinessRuleException("请先打开出库单。"); string E(object? v) => WebUtility.HtmlEncode(Convert.ToString(v, CultureInfo.InvariantCulture) ?? "");
        var rows = string.Join("", d.Checks.Select(c => $"<tr><td>{E(c.SkuCode)}</td><td>{E(c.Name)}</td><td>{E(c.Required)}</td><td>{E(c.Verified)}</td><td>□</td></tr>"));
        var lines = string.Join("", d.Lines.Select(l => $"<tr><td>{E(l.ExternalOrderId)}</td><td>{E(l.ExternalLineId)}</td><td>{E(l.SkuCode)}</td><td>{E(l.Quantity)}</td></tr>"));
        return $"<!doctype html><html lang='zh-CN'><meta charset='utf-8'><title>{E(d.Summary.Number)} 拣货单</title><style>body{{font:14px 'Microsoft YaHei',sans-serif;margin:32px;color:#172b4d}}table{{width:100%;border-collapse:collapse;margin:16px 0}}th,td{{border:1px solid #bbc6d6;padding:9px;text-align:left}}h1{{font-size:24px}}small{{color:#53657d}}@media print{{body{{margin:10mm}}}}</style><h1>拣货 / 复核单 · {E(d.Summary.Number)}</h1><p>示例主仓 · 操作人：{E(Actor)} · 导出时间：{E(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"))}</p><p>{E(d.Address.Recipient)} · {E(d.Address.Phone)}<br>{E(d.Address.Province)} {E(d.Address.City)} {E(d.Address.District)} {E(d.Address.Street)}</p><p>承运商：{E(d.Summary.Carrier)}　运单：{E(d.Summary.TrackingNumber)}</p><h2>实物组件</h2><table><tr><th>SKU 编码</th><th>商品</th><th>应拣</th><th>已复核</th><th>人工核对</th></tr>{rows}</table><h2>原订单行</h2><table><tr><th>原订单号</th><th>原行号</th><th>销售 SKU</th><th>本包数量</th></tr>{lines}</table><p>拣货人：________　复核人：________　交接人：________</p><small>本单为仓内作业凭据，不代表平台已确认发货，也不是物流商电子面单。当前为本地模拟工作区。</small></html>";
    }
}
