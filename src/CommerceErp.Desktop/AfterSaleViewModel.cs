using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows.Input;
using CommerceErp.Core;

namespace CommerceErp.Desktop;

public sealed record AfterKindOption(AfterSaleKind Value, string Name);
public sealed record RefundStateOption(RefundTrackingState Value, string Name);
public sealed record AfterStateOption(AfterSaleState? Value, string Name);
public sealed record AfterCaseRow(AfterSaleSummary Source)
{
    public string Kind => AfterSaleViewModel.KindName(Source.Kind);
    public string State => AfterSaleViewModel.StateName(Source.State);
    public string Refund => AfterSaleViewModel.RefundName(Source.RefundState);
}
public sealed class AfterSourceRow(AfterSaleSourceLine source) : ObservableObject
{
    public AfterSaleSourceLine Source { get; } = source;
    private string _quantity = "0";
    public string Quantity { get => _quantity; set => Set(ref _quantity, value); }
}
public sealed class InspectionRow(ReturnComponentView source) : ObservableObject
{
    public ReturnComponentView Source { get; } = source;
    private string _good = "0", _damaged = "0";
    public string Good { get => _good; set => Set(ref _good, value); }
    public string Damaged { get => _damaged; set => Set(ref _damaged, value); }
}
public sealed record RefundObservationRow(RefundObservationView Source)
{ public string State => AfterSaleViewModel.RefundName(Source.Status); }

public sealed class AfterSaleViewModel(LocalApiClient api) : ObservableObject
{
    public ObservableCollection<AfterSaleCandidate> Candidates { get; } = [];
    public ObservableCollection<AfterSourceRow> SourceLines { get; } = [];
    public ObservableCollection<AfterCaseRow> Cases { get; } = [];
    public ObservableCollection<InspectionRow> Inspection { get; } = [];
    public ObservableCollection<RefundObservationRow> Refunds { get; } = [];
    public ObservableCollection<QuarantineRow> Quarantine { get; } = [];
    public ObservableCollection<QuarantineMovement> QuarantineLedger { get; } = [];
    public List<AfterKindOption> Kinds { get; } = [new(AfterSaleKind.CancelUnshipped, "未出库整单取消"), new(AfterSaleKind.RefundOnly, "已出库仅退款"), new(AfterSaleKind.ReturnRefund, "退货退款")];
    public List<RefundStateOption> RefundStates { get; } = [new(RefundTrackingState.Unknown, "结果不明 / 待回查"), new(RefundTrackingState.Failed, "观察到退款失败"), new(RefundTrackingState.EvidenceRecorded, "人工凭证登记（未平台核验）")];
    public List<AfterStateOption> Filters { get; } = [new(null, "全部状态"), new(AfterSaleState.Open, "待审核"), new(AfterSaleState.Approved, "处理中"), new(AfterSaleState.Closed, "本地已结案"), new(AfterSaleState.Rejected, "已驳回"), new(AfterSaleState.Withdrawn, "已撤回")];
    private bool _busy, _returned, _short, _confirmEvidence;
    public bool IsBusy { get => _busy; set { Set(ref _busy, value); Changed(nameof(IsReady)); CommandManager.InvalidateRequerySuggested(); } }
    public bool IsReady => !IsBusy;
    public bool ConfirmReturned { get => _returned; set => Set(ref _returned, value); }
    public bool AcceptShort { get => _short; set => Set(ref _short, value); }
    public bool ConfirmEvidence { get => _confirmEvidence; set => Set(ref _confirmEvidence, value); }
    private int _tab, _page = 1, _total;
    public int Tab { get => _tab; set => Set(ref _tab, value); }
    public string PageText => $"第 {_page} 页 · 共 {_total} 张售后单";
    private string _actor = Environment.UserName, _reason = "", _search = "", _orderSearch = "", _message = "从原订单创建售后申请；申请提交后立即拦截相关订单发货。";
    public string Actor { get => _actor; set => Set(ref _actor, value); }
    public string Reason { get => _reason; set => Set(ref _reason, value); }
    public string Search { get => _search; set => Set(ref _search, value); }
    public string OrderSearch { get => _orderSearch; set => Set(ref _orderSearch, value); }
    public string Message { get => _message; set => Set(ref _message, value); }
    public string PendingText => $"恢复未确认操作（{api.PendingCount}）";
    private AfterKindOption? _kind;
    public AfterKindOption? Kind
    {
        get => _kind ?? Kinds[2];
        set { Set(ref _kind, value); Changed(nameof(IsCancel)); if (IsCancel && Preparation is not null) RefundAmount = Preparation.Order.PaidAmount.ToString("0.00", CultureInfo.InvariantCulture); }
    }
    public bool IsCancel => Kind?.Value == AfterSaleKind.CancelUnshipped;
    private RefundStateOption? _refundState;
    public RefundStateOption? RefundState { get => _refundState ?? RefundStates[0]; set => Set(ref _refundState, value); }
    private AfterStateOption? _filter;
    public AfterStateOption? Filter { get => _filter ?? Filters[0]; set => Set(ref _filter, value); }
    private AfterSaleCandidate? _candidate;
    public AfterSaleCandidate? SelectedCandidate { get => _candidate; set { Set(ref _candidate, value); CommandManager.InvalidateRequerySuggested(); } }
    private AfterSalePreparation? _preparation;
    public AfterSalePreparation? Preparation { get => _preparation; set => Set(ref _preparation, value); }
    private AfterCaseRow? _selectedCase;
    public AfterCaseRow? SelectedCase { get => _selectedCase; set { Set(ref _selectedCase, value); if (value is not null && Details?.Summary.Id != value.Source.Id) Details = null; } }
    private AfterSaleDetails? _details;
    public AfterSaleDetails? Details
    {
        get => _details;
        set { Set(ref _details, value); if (value is null) { Inspection.Clear(); Refunds.Clear(); } foreach (var name in new[] { nameof(DetailTitle), nameof(DetailStatus), nameof(IsOpen), nameof(IsApproved), nameof(IsReturn), nameof(IsCancellation), nameof(CanInspect), nameof(CanRecordRefund), nameof(CanWithdraw) }) Changed(name); CommandManager.InvalidateRequerySuggested(); }
    }
    public string DetailTitle => Details is null ? "打开一张售后单" : Details.Summary.Number + " · " + KindName(Details.Summary.Kind);
    public string DetailStatus => Details is null ? "在处理页选中并打开售后单，其他页签共用该单。" : $"{StateName(Details.Summary.State)} · {RefundName(Details.Summary.RefundState)} · v{Details.Summary.Version}";
    public bool IsOpen => Details?.Summary.State == AfterSaleState.Open;
    public bool IsApproved => Details?.Summary.State == AfterSaleState.Approved;
    public bool IsReturn => Details?.Summary.Kind == AfterSaleKind.ReturnRefund;
    public bool IsCancellation => Details?.Summary.Kind == AfterSaleKind.CancelUnshipped;
    public bool CanInspect => IsApproved && IsReturn;
    public bool CanRecordRefund => IsApproved && Details?.Summary.RefundState != RefundTrackingState.EvidenceRecorded;
    public bool CanWithdraw => IsOpen || (IsApproved && Details!.Summary.Kind != AfterSaleKind.CancelUnshipped && Details.Receipts.Count == 0 && Details.Summary.RefundState is RefundTrackingState.Pending or RefundTrackingState.Failed);
    private string _sourceNo = NewNumber("LOCAL"), _refundAmount = "0.00", _receiptNo = NewNumber("RCV"), _tracking = "", _eventKey = NewNumber("OBS"), _reference = "", _evidence = "", _observedAt = DateTimeOffset.Now.ToString("O");
    private static string NewNumber(string prefix) => prefix + "-" + Guid.NewGuid().ToString("N");
    public string SourceNumber { get => _sourceNo; set => Set(ref _sourceNo, value); }
    public string RefundAmount { get => _refundAmount; set => Set(ref _refundAmount, value); }
    public string ReceiptNumber { get => _receiptNo; set => Set(ref _receiptNo, value); }
    public string ReturnTracking { get => _tracking; set => Set(ref _tracking, value); }
    public string EventKey { get => _eventKey; set => Set(ref _eventKey, value); }
    public string RefundReference { get => _reference; set => Set(ref _reference, value); }
    public string Evidence { get => _evidence; set => Set(ref _evidence, value); }
    public string ObservedAt { get => _observedAt; set => Set(ref _observedAt, value); }
    private bool HasReason => !string.IsNullOrWhiteSpace(Reason);
    private ICommand Command(Func<Task> action, Func<bool>? enabled = null, string? permission = null) => new AsyncCommand(async () =>
    { IsBusy = true; try { await action(); } finally { IsBusy = false; Changed(nameof(PendingText)); } }, e => Message = "操作未完成：" + e.Message,
        () => !IsBusy && (permission is null || api.Allows(permission)) && !string.IsNullOrWhiteSpace(Actor) && (enabled?.Invoke() ?? true));
    public ICommand RefreshCommand => Command(async () => { _page = 1; await RefreshAsync(); });
    public ICommand PreviousCommand => Command(async () => { _page--; await RefreshAsync(); }, () => _page > 1);
    public ICommand NextCommand => Command(async () => { _page++; await RefreshAsync(); }, () => _page * 20 < _total);
    public ICommand OpenOrderCommand => Command(OpenOrderAsync, () => SelectedCandidate is not null);
    public ICommand CreateCommand => Command(CreateAsync, () => HasReason && Preparation is not null && Preparation.Order.OrderId == SelectedCandidate?.OrderId, permission:"aftersales.manage");
    public ICommand OpenCaseCommand => Command(async () => { if (SelectedCase is not null) await OpenCaseAsync(SelectedCase.Source.Id); }, () => SelectedCase is not null);
    public ICommand ApproveCommand => Command(() => ActAsync("approve"), () => HasReason && IsOpen, permission:"aftersales.manage");
    public ICommand RejectCommand => Command(() => ActAsync("reject"), () => HasReason && IsOpen, permission:"aftersales.manage");
    public ICommand WithdrawCommand => Command(() => ActAsync("withdraw"), () => HasReason && CanWithdraw, permission:"aftersales.manage");
    public ICommand InspectCommand => Command(InspectAsync, () => HasReason && CanInspect, permission:"aftersales.inspect");
    public ICommand RefundCommand => Command(RefundAsync, () => HasReason && CanRecordRefund, permission:"aftersales.refund");
    public ICommand CloseCommand => Command(() => ActAsync("close"), () => HasReason && IsApproved && Details?.Summary.RefundState == RefundTrackingState.EvidenceRecorded, permission:"aftersales.manage");
    public ICommand AuditCommand => new ScopedCommand(Command(AuditAsync, permission:"inventory.read"),()=>api.HasAllShops);
    public ICommand RecoverCommand => Command(async () => { await api.RetryPendingAsync(); await RefreshAsync(); Message = "已重放保存的原请求，请核对最新状态。"; }, () => api.PendingCount > 0);
    public async Task RefreshAsync()
    {
        var chosenId = SelectedCandidate?.OrderId;
        Candidates.Clear(); foreach (var item in await api.GetAsync<List<AfterSaleCandidate>>("/api/after-sales/orders?search=" + Uri.EscapeDataString(OrderSearch))) Candidates.Add(item);
        if (Preparation is not null)
        {
            if (Candidates.Any(c => c.OrderId == Preparation.Order.OrderId)) await LoadOrderAsync(Preparation.Order.OrderId);
            else { Preparation = null; SourceLines.Clear(); }
        }
        SelectedCandidate = Candidates.FirstOrDefault(c => c.OrderId == chosenId) ?? Candidates.FirstOrDefault();
        var detailId = Details?.Summary.Id;
        var page = await api.GetAsync<AfterSalePage>($"/api/after-sales/cases?page={_page}&pageSize=20&search={Uri.EscapeDataString(Search)}" + (Filter?.Value is null ? "" : "&state=" + Filter.Value));
        Cases.Clear(); foreach (var row in page.Items) Cases.Add(new(row)); _total = page.Total; Changed(nameof(PageText));
        if(api.Allows("inventory.read")) {
        Quarantine.Clear(); foreach (var row in await api.GetAsync<List<QuarantineRow>>("/api/after-sales/quarantine")) Quarantine.Add(row);
        QuarantineLedger.Clear(); foreach (var row in await api.GetAsync<List<QuarantineMovement>>("/api/after-sales/quarantine/ledger")) QuarantineLedger.Add(row);
        }
        if (detailId is not null) await LoadCaseAsync(detailId);
        Changed(nameof(PendingText));
    }
    public async Task OpenOrderAsync() { if (SelectedCandidate is not null) await LoadOrderAsync(SelectedCandidate.OrderId); }
    private async Task LoadOrderAsync(string id)
    {
        Preparation = await api.GetAsync<AfterSalePreparation>("/api/after-sales/orders/" + id);
        SourceLines.Clear(); foreach (var row in Preparation.Lines) SourceLines.Add(new(row));
        RefundAmount = (IsCancel ? Preparation.Order.PaidAmount : Preparation.Order.RemainingRefundAmount).ToString("0.00", CultureInfo.InvariantCulture);
    }
    private static decimal Number(string text, int places) => decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var n) && n >= 0 && decimal.Round(n, places) == n ? n : throw new BusinessRuleException($"请输入非负数，最多 {places} 位小数。");
    public async Task CreateAsync()
    {
        if (Preparation is null) return;
        var lines = IsCancel ? [] : SourceLines.Select(l => new AfterSaleSelection(l.Source.ShipmentLineId, Number(l.Quantity, 3))).Where(l => l.Quantity > 0).ToList();
        var result = await api.PostCommandAsync<AfterSaleResult>("/api/after-sales/cases", new { orderId = Preparation.Order.OrderId, expectedOrderVersion = Preparation.Order.Version, sourceCaseNumber = SourceNumber, kind = Kind!.Value, refundAmount = Number(RefundAmount, 2), lines, actor = Actor, reason = Reason });
        SourceNumber = NewNumber("LOCAL"); await RefreshAsync(); await OpenCaseAsync(result.Id); Message = result.Message;
    }
    private async Task LoadCaseAsync(string id)
    {
        Details = await api.GetAsync<AfterSaleDetails>("/api/after-sales/cases/" + id);
        Inspection.Clear(); foreach (var row in Details.Components) Inspection.Add(new(row));
        Refunds.Clear(); foreach (var row in Details.RefundObservations) Refunds.Add(new(row));
    }
    public async Task OpenCaseAsync(string id)
    { await LoadCaseAsync(id); Tab = 1; ConfirmReturned = false; AcceptShort = false; ConfirmEvidence = false; EventKey = NewNumber("OBS"); ReceiptNumber = NewNumber("RCV"); ReturnTracking = ""; RefundReference = ""; Evidence = ""; ObservedAt = DateTimeOffset.Now.ToString("O"); }
    public async Task ActAsync(string action)
    {
        if (Details is null) return; var id = Details.Summary.Id;
        var result = await api.PostCommandAsync<AfterSaleResult>($"/api/after-sales/cases/{id}/{action}", new { expectedVersion = Details.Summary.Version, actor = Actor, reason = Reason, confirmPickedGoodsReturned = ConfirmReturned, acceptShortReturn = AcceptShort });
        await RefreshAsync(); Message = result.Message;
    }
    public async Task InspectAsync()
    {
        if (Details is null) return;
        var lines = Inspection.Select(l => new InspectionLine(l.Source.CaseLineId, l.Source.SkuId, Number(l.Good, 3), Number(l.Damaged, 3))).Where(l => l.GoodQuantity + l.DamagedQuantity > 0).ToList();
        var result = await api.PostCommandAsync<AfterSaleResult>($"/api/after-sales/cases/{Details.Summary.Id}/inspect", new { expectedVersion = Details.Summary.Version, receiptNumber = ReceiptNumber, returnTracking = ReturnTracking, lines, actor = Actor, reason = Reason });
        ReceiptNumber = NewNumber("RCV"); await RefreshAsync(); Message = result.Message;
    }
    public async Task RefundAsync()
    {
        if (Details is null) return;
        if (!Regex.IsMatch(ObservedAt.Trim(), "(?:[zZ]|[+-][0-9]{2}:[0-9]{2})$") || !DateTimeOffset.TryParse(ObservedAt, CultureInfo.InvariantCulture, DateTimeStyles.None, out var observed)) throw new BusinessRuleException("观察时间须含时区，例如 2026-10-28T14:30:00+08:00。");
        var result = await api.PostCommandAsync<AfterSaleResult>($"/api/after-sales/cases/{Details.Summary.Id}/refund", new { expectedVersion = Details.Summary.Version, eventKey = EventKey, status = RefundState!.Value, amount = Details.Summary.RefundAmount, refundReference = RefundReference, evidence = Evidence, observedAt = observed, confirmLocalEvidence = ConfirmEvidence, actor = Actor, reason = Reason });
        EventKey = NewNumber("OBS"); ObservedAt = DateTimeOffset.Now.ToString("O"); ConfirmEvidence = false; await RefreshAsync(); Message = result.Message;
    }
    public async Task AuditAsync()
    {
        var a = await api.GetAsync<AfterSaleAudit>("/api/after-sales/audit"); var b = await api.GetAsync<ConsistencyReport>("/api/business/consistency");
        Message = a.Passed && b.Passed ? $"检查通过：{a.Cases} 张售后单、{a.Receipts} 个收货批次；数量、凭证和库存流水相符。" : "发现差异：" + string.Join("；", a.Problems.Concat(b.Problems).Distinct());
    }
    public static string KindName(AfterSaleKind k) => k switch { AfterSaleKind.CancelUnshipped => "未出库整单取消", AfterSaleKind.RefundOnly => "已出库仅退款", _ => "退货退款" };
    public static string StateName(AfterSaleState s) => s switch { AfterSaleState.Open => "待审核", AfterSaleState.Approved => "处理中", AfterSaleState.Closed => "本地已结案", AfterSaleState.Rejected => "已驳回", _ => "已撤回" };
    public static string RefundName(RefundTrackingState s) => s switch { RefundTrackingState.Pending => "待登记", RefundTrackingState.Unknown => "结果不明 / 待回查", RefundTrackingState.Failed => "已记录失败", _ => "人工凭证已登记（未核验）" };
}
