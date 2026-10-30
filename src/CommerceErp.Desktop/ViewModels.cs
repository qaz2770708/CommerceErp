using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Input;
using CommerceErp.Core;
using Microsoft.Win32;

namespace CommerceErp.Desktop;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    protected bool Set<T>(ref T storage, T value, [CallerMemberName] string? name = null)
    { if (EqualityComparer<T>.Default.Equals(storage, value)) return false; storage = value; Changed(name); return true; }
}

public sealed class ActionCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;
    public void Execute(object? parameter) => execute();
    public event EventHandler? CanExecuteChanged { add => CommandManager.RequerySuggested += value; remove => CommandManager.RequerySuggested -= value; }
}

public sealed class AsyncCommand(Func<Task> execute, Action<Exception> onError, Func<bool>? canExecute = null) : ICommand
{
    private bool _running;
    public bool CanExecute(object? parameter) => !_running && (canExecute?.Invoke() ?? true);
    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        _running = true; CommandManager.InvalidateRequerySuggested();
        try { await execute(); } catch (Exception e) { onError(e); }
        finally { _running = false; CommandManager.InvalidateRequerySuggested(); }
    }
    public event EventHandler? CanExecuteChanged { add => CommandManager.RequerySuggested += value; remove => CommandManager.RequerySuggested -= value; }
}

public sealed class ScopedCommand(ICommand inner,Func<bool> scope):ICommand
{
    public bool CanExecute(object? p)=>scope()&&inner.CanExecute(p);
    public void Execute(object? p){if(CanExecute(p))inner.Execute(p);}
    public event EventHandler? CanExecuteChanged {add=>inner.CanExecuteChanged+=value;remove=>inner.CanExecuteChanged-=value;}
}
public sealed record StatusOption(CheckStatus Value, string Name);
public sealed class CheckItemViewModel(CheckDefinition definition, CheckEntry entry) : ObservableObject
{
    public string Key => definition.Key;
    public string Title => definition.Title;
    public string Guidance => definition.Guidance;
    public string ReferenceUrl => definition.ReferenceUrl;
    private CheckStatus _status = entry.Status;
    public CheckStatus Status { get => _status; set { if (Set(ref _status, value)) Changed(nameof(StatusText)); } }
    public string StatusText => Status switch { CheckStatus.Verified => "已核实", CheckStatus.InProgress => "核实中", CheckStatus.Blocked => "有阻碍", _ => "待核实" };
    private string _evidence = entry.Evidence;
    public string Evidence { get => _evidence; set => Set(ref _evidence, value); }
}

public sealed record MappingRow(SkuMapping Source, string PlatformName, string SkuCode, string SkuName)
{
    public string TenantId => Source.TenantId;
    public string ShopId => Source.ShopId;
    public string ProductId => Source.PlatformProductId;
    public string PlatformSkuId => Source.PlatformSkuId;
    public decimal Multiplier => Source.UnitMultiplier;
    public int Version => Source.Version;
}

public sealed class MainViewModel(LocalApiClient api) : ObservableObject
{
    private BootstrapData? _bootstrap;
    private int _readinessRevision;
    public ObservableCollection<PlatformInfo> Platforms { get; } = [];
    public ObservableCollection<CheckItemViewModel> Checks { get; } = [];
    public ObservableCollection<MappingRow> Mappings { get; } = [];
    public ObservableCollection<InternalSku> Skus { get; } = [];
    public ObservableCollection<SampleDescriptor> Samples { get; } = [];
    public ObservableCollection<ValidationIssue> Issues { get; } = [];
    public ObservableCollection<ValidationReport> Reports { get; } = [];
    public List<StatusOption> StatusOptions { get; } = [new(CheckStatus.NotChecked, "待核实"), new(CheckStatus.InProgress, "核实中"), new(CheckStatus.Verified, "已核实"), new(CheckStatus.Blocked, "有阻碍")];

    private PlatformInfo? _selectedPlatform;
    public PlatformInfo? SelectedPlatform
    {
        get => _selectedPlatform;
        set
        {
            if (!Set(ref _selectedPlatform, value)) return;
            if (_bootstrap is not null) LoadPlatform();
        }
    }
    private CheckItemViewModel? _selectedCheck;
    public CheckItemViewModel? SelectedCheck { get => _selectedCheck; set => Set(ref _selectedCheck, value); }
    private MappingRow? _selectedMapping;
    public MappingRow? SelectedMapping
    {
        get => _selectedMapping;
        set
        {
            if (!Set(ref _selectedMapping, value) || value is null) return;
            MappingTenant = value.TenantId; MappingShop = value.ShopId; MappingProduct = value.ProductId;
            MappingVariant = value.PlatformSkuId; Multiplier = value.Multiplier.ToString(CultureInfo.InvariantCulture);
            SelectedSku = Skus.FirstOrDefault(s => s.Id == value.Source.InternalSkuId);
            Changed(nameof(IsEditingMapping)); Changed(nameof(MappingEditorTitle));
        }
    }
    public bool IsEditingMapping => SelectedMapping is not null;
    public string MappingEditorTitle => IsEditingMapping ? $"编辑映射 · v{SelectedMapping!.Version}" : "新建 SKU 映射";
    private string _mappingTenant = SeedData.Tenant;
    public string MappingTenant { get => _mappingTenant; set => Set(ref _mappingTenant, value); }
    private string _mappingShop = "";
    public string MappingShop { get => _mappingShop; set => Set(ref _mappingShop, value); }
    private string _mappingProduct = "";
    public string MappingProduct { get => _mappingProduct; set => Set(ref _mappingProduct, value); }
    private string _mappingVariant = "";
    public string MappingVariant { get => _mappingVariant; set => Set(ref _mappingVariant, value); }
    private string _multiplier = "1";
    public string Multiplier { get => _multiplier; set => Set(ref _multiplier, value); }
    private InternalSku? _selectedSku;
    public InternalSku? SelectedSku { get => _selectedSku; set => Set(ref _selectedSku, value); }
    private SampleDescriptor? _selectedSample;
    public SampleDescriptor? SelectedSample { get => _selectedSample; set => Set(ref _selectedSample, value); }
    private string _inputJson = "";
    public string InputJson { get => _inputJson; set => Set(ref _inputJson, value); }
    private string _outputJson = "载入一个模拟订单，然后运行模型校验。";
    public string OutputJson { get => _outputJson; set => Set(ref _outputJson, value); }
    private string _resultTitle = "等待校验";
    public string ResultTitle { get => _resultTitle; set => Set(ref _resultTitle, value); }
    private string _resultDetail = "通过只表示样例符合当前模型，不会创建业务订单或预占库存。";
    public string ResultDetail { get => _resultDetail; set => Set(ref _resultDetail, value); }
    private string _resultColor = "#63758C";
    public string ResultColor { get => _resultColor; set => Set(ref _resultColor, value); }
    private string _message = "正在连接服务…";
    public string Message { get => _message; set => Set(ref _message, value); }
    private bool _isBusy;
    public bool IsBusy { get => _isBusy; set { Set(ref _isBusy, value); Changed(nameof(IsReady)); CommandManager.InvalidateRequerySuggested(); } }
    public bool IsReady => !IsBusy && _bootstrap is not null;
    public string ConnectionLabel => api.ConnectionLabel;
    public string AccountLabel => api.CurrentAccount?.DisplayName + " · " + api.CurrentAccount?.Username;
    public bool IsAdministrator => api.Allows("security.manage");
    public ICommand MasterCommand => new AsyncCommand(async()=>{var dialog=new MasterWindow(api){Owner=System.Windows.Application.Current.MainWindow};await dialog.RefreshAsync();dialog.ShowDialog();await InitializeAsync();},ShowError,()=>!IsBusy&&api.Allows("master.read"));
    public string WorkspaceNotice=>_bootstrap?.Workspace?.Mode=="Business"?"正式资料工作区：平台尚未连接；人工订单与期初库存需要核实后提交。":"演示工作区：仓储和退货会改变库存；真实平台尚未接通。";
    public ICommand TasksCommand => new AsyncCommand(async()=>{var dialog=new WorkTaskWindow(api){Owner=System.Windows.Application.Current.MainWindow};await dialog.RefreshAsync();dialog.ShowDialog();},ShowError,()=>!IsBusy&&api.Allows("tasks.read"));
    public ICommand AccountsCommand => new AsyncCommand(async()=>{var dialog=new AccountWindow(api){Owner=System.Windows.Application.Current.MainWindow}; await dialog.RefreshAsync();dialog.ShowDialog();},ShowError,()=>!IsBusy&&IsAdministrator);
    public ICommand LogoutCommand => new AsyncCommand(async()=>{try {await api.LogoutAsync();} finally {System.Windows.Application.Current.MainWindow.Close();}},ShowError,()=>!IsBusy);
    public ICommand PasswordCommand => new ActionCommand(()=>new PasswordWindow(api){Owner=System.Windows.Application.Current.MainWindow}.ShowDialog(),()=>!IsBusy);
    public ICommand ConnectionSettingsCommand => new ActionCommand(() => { var window = new ConnectionWindow { Owner = System.Windows.Application.Current.MainWindow }; window.ShowDialog(); }, () => !IsBusy);
    public OperationsViewModel Operations { get; } = new(api);
    public WarehouseViewModel Warehouse { get; } = new(api);
    public AfterSaleViewModel AfterSales { get; } = new(api);
    public FinanceViewModel Finance { get; } = new(api);
    private string _page = "orders";
    public bool IsOrders => _page == "orders";
    public bool IsInventory => _page == "inventory";
    public bool IsWarehouse => _page == "warehouse";
    public bool IsFinance => _page == "finance";
    public bool IsAfterSales => _page == "after-sales";
    public bool IsReadiness => _page == "readiness";
    public bool IsMappings => _page == "mappings";
    public bool IsValidation => _page == "validation";
    public string PageTitle => _page switch { "finance" => "业财对账与利润", "after-sales" => "售后与逆向", "warehouse" => "仓储发货", "orders" => "订单中心", "inventory" => "库存与可承诺量", "mappings" => "建立统一的 SKU 语言", "validation" => "验证订单，解释每一个差异", _ => "从可靠的接入开始" };
    public string PageDescription => _page switch
    {
        "finance" => "核对订单与账单，保留成本快照，追踪每一处结算差异。",
        "after-sales" => "拦截发货、退货质检与退款跟踪；让实物和资金记录各自有据可查。",
        "warehouse" => "配单、拆合包裹、扫码复核与出库；按原订单跟踪平台回传。",
        "orders" => "统一接入、规则审单、整单预占与取消释放。",
        "inventory" => "按货主、仓库与 SKU 管理余额，追溯每一次库存变化。",
        "mappings" => "以租户、平台和店铺隔离映射；每次修改保留新的版本号。",
        "validation" => "用模拟订单验证金额口径、标识、SKU 映射和组合商品；不调用平台 API。",
        _ => "逐项记录资质、权限与部署条件，为真实平台联调准备依据。"
    };
    public int VerifiedCount => Checks.Count(c => c.Status == CheckStatus.Verified);
    public string ProgressText => $"{VerifiedCount} / {Checks.Count}";
    public string MappingCount => Mappings.Count.ToString();
    public string ReportCount => Reports.Count.ToString();
    public string QualificationNote => VerifiedCount == Checks.Count && Checks.Count > 0 ? "人工记录已齐备；仍需真实店铺联调与平台验收。" : "待核实的项目会阻止进入真实接入；这里只记录人工核实结果。";
    public ValidationReport? LastReport { get; private set; }
    private ValidationReport? _selectedReport;
    public ValidationReport? SelectedReport { get => _selectedReport; set => Set(ref _selectedReport, value); }

    public ICommand ReadinessPageCommand => new ActionCommand(() => Navigate("readiness"), () => api.Allows("security.manage") && !IsBusy);
    public ICommand OrdersPageCommand => new AsyncCommand(() => NavigateBusinessAsync("orders"), ShowError, () => !IsBusy && !Operations.IsBusy && !Warehouse.IsBusy && !AfterSales.IsBusy && !Finance.IsBusy);
    public ICommand InventoryPageCommand => new AsyncCommand(() => NavigateBusinessAsync("inventory"), ShowError, () => api.Allows("inventory.read") && !IsBusy && !Operations.IsBusy && !Warehouse.IsBusy && !AfterSales.IsBusy && !Finance.IsBusy);
    public ICommand WarehousePageCommand => new AsyncCommand(() => NavigateBusinessAsync("warehouse"), ShowError, () => api.Allows("warehouse.read") && !IsBusy && !Operations.IsBusy && !Warehouse.IsBusy && !AfterSales.IsBusy && !Finance.IsBusy);
    public ICommand AfterSalesPageCommand => new AsyncCommand(() => NavigateBusinessAsync("after-sales"), ShowError, () => !IsBusy && !Operations.IsBusy && !Warehouse.IsBusy && !AfterSales.IsBusy && !Finance.IsBusy);
    public ICommand FinancePageCommand => new AsyncCommand(() => NavigateBusinessAsync("finance"), ShowError, () => api.Allows("finance.read") && !IsBusy && !Operations.IsBusy && !Warehouse.IsBusy && !AfterSales.IsBusy && !Finance.IsBusy);
    private async Task NavigateBusinessAsync(string page)
    {
        IsBusy = true;
        try { Navigate(page); if (page == "finance") await Finance.RefreshAsync(); else if (page == "warehouse") await Warehouse.RefreshAsync(); else if (page == "after-sales") await AfterSales.RefreshAsync(); else await Operations.RefreshAsync(); }
        finally { IsBusy = false; }
    }
    public ICommand MappingsPageCommand => new ActionCommand(() => Navigate("mappings"), () => api.Allows("security.manage") && !IsBusy);
    public ICommand ValidationPageCommand => new ActionCommand(() => Navigate("validation"), () => api.Allows("security.manage") && !IsBusy);
    public ICommand SaveReadinessCommand => new AsyncCommand(SaveReadinessAsync, ShowError, () => IsReady);
    public ICommand SaveMappingCommand => new AsyncCommand(SaveMappingAsync, ShowError, () => IsReady);
    public ICommand NewMappingCommand => new ActionCommand(NewMapping, () => IsReady);
    public ICommand LoadSampleCommand => new AsyncCommand(LoadSampleAsync, ShowError, () => IsReady && SelectedSample is not null);
    public ICommand ValidateCommand => new AsyncCommand(ValidateAsync, ShowError, () => IsReady && !string.IsNullOrWhiteSpace(InputJson));
    public ICommand ExportCommand => new AsyncCommand(ExportAsync, ShowError, () => IsReady && LastReport is not null);
    public ICommand ViewReportCommand => new ActionCommand(() => { if (SelectedReport is not null) ShowReport(SelectedReport); }, () => SelectedReport is not null && !IsBusy);

    public void Navigate(string page)
    {
        _page = page;
        if (page == "warehouse") Message = "仓内出库与平台确认分别记录；操作人由当前登录账号确认。";
        if (page == "finance") Message = "账单来源与退款均未平台自动核验；基础结算贡献不是完整经营净利润。";
        if (page == "after-sales") Message = "质检入账会改变本地库存；退款记录为人工凭证，未经系统平台核验。";
        foreach (var name in new[] { nameof(IsFinance), nameof(IsAfterSales), nameof(IsWarehouse), nameof(IsOrders), nameof(IsInventory), nameof(IsReadiness), nameof(IsMappings), nameof(IsValidation), nameof(PageTitle), nameof(PageDescription) }) Changed(name);
    }

    public async Task InitializeAsync()
    {
        IsBusy = true;
        try
        {
            _bootstrap = await api.GetAsync<BootstrapData>("/api/bootstrap");
            Platforms.Clear();Skus.Clear();Samples.Clear();
            if(_bootstrap.Workspace is{} profile)api.SetWorkspace(profile);Changed(nameof(WorkspaceNotice));
            foreach (var p in _bootstrap.Platforms) Platforms.Add(p);
            foreach (var sku in _bootstrap.Catalog.Skus.Where(s => s.Enabled)) Skus.Add(sku);
            foreach (var sample in _bootstrap.Samples) Samples.Add(sample);
            SelectedPlatform = Platforms.FirstOrDefault(); SelectedSample = Samples.FirstOrDefault();
            await LoadSampleCoreAsync();
            if(api.Allows("security.manage")) await RefreshReportsAsync();
            await Operations.InitializeAsync(_bootstrap.Samples);
            if(api.Allows("warehouse.read")) await Warehouse.RefreshAsync();
            await AfterSales.RefreshAsync();
            if(api.Allows("finance.read")) await Finance.RefreshAsync();
            Warehouse.Actor=AfterSales.Actor=Finance.Actor=api.CurrentAccount!.Username;
            Message = api.ConnectionLabel + "已就绪 · "+WorkspaceNotice;
        }
        finally { IsBusy = false; }
    }

    private void LoadPlatform()
    {
        if (_bootstrap is null || SelectedPlatform is null) return;
        var current = _bootstrap.Readiness.Single(r => r.Platform == SelectedPlatform.Id);
        _readinessRevision = current.Revision;
        Checks.Clear();
        foreach (var definition in _bootstrap.Checklists[SelectedPlatform.Id])
        {
            var vm = new CheckItemViewModel(definition, current.Items.Single(i => i.Key == definition.Key));
            vm.PropertyChanged += (_, _) =>
            {
                // Keep unsaved edits per platform when switching tabs. Revision stays server-owned.
                var index = current.Items.FindIndex(i => i.Key == vm.Key);
                current.Items[index] = new(vm.Key, vm.Status, vm.Evidence);
                Changed(nameof(ProgressText)); Changed(nameof(QualificationNote));
                Message = "存在未保存的接入记录，请点击“保存核实记录”。";
            };
            Checks.Add(vm);
        }
        SelectedCheck = Checks.First();
        Mappings.Clear();
        foreach (var mapping in _bootstrap.Catalog.Mappings.Where(m => m.Platform == SelectedPlatform.Id))
        {
            var sku = _bootstrap.Catalog.Skus.Single(s => s.Id == mapping.InternalSkuId);
            Mappings.Add(new(mapping, SelectedPlatform.Name, sku.Code, sku.Name));
        }
        NewMapping();
        Changed(nameof(ProgressText)); Changed(nameof(QualificationNote)); Changed(nameof(MappingCount));
    }

    public async Task SaveReadinessAsync()
    {
        if (SelectedPlatform is null || _bootstrap is null) return;
        IsBusy = true;
        try
        {
            var saved = await api.PutAsync<PlatformReadiness>($"/api/readiness/{SelectedPlatform.Id}",
                new ReadinessUpdate(_readinessRevision, Checks.Select(c => new CheckEntry(c.Key, c.Status, c.Evidence)).ToList()));
            var index = _bootstrap.Readiness.FindIndex(r => r.Platform == saved.Platform);
            _bootstrap.Readiness[index] = saved; LoadPlatform();
            Message = "核实记录已保存。状态由人工记录，未触发平台申请或店铺授权。";
        }
        finally { IsBusy = false; }
    }
    public void NewMapping()
    {
        SelectedMapping = null; MappingTenant = _bootstrap?.Workspace?.TenantId??SeedData.Tenant; MappingShop = _bootstrap?.Workspace?.Shops.FirstOrDefault(s=>s.Platform==SelectedPlatform?.Id)?.Id??"";
        MappingProduct = ""; MappingVariant = ""; Multiplier = "1"; SelectedSku = Skus.FirstOrDefault();
        Changed(nameof(IsEditingMapping)); Changed(nameof(MappingEditorTitle));
    }
    public async Task SaveMappingAsync()
    {
        if (SelectedPlatform is null || SelectedSku is null || _bootstrap is null) return;
        if (!decimal.TryParse(Multiplier, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var multiplier))
            throw new BusinessRuleException("请输入合法倍率，使用小数点，例如 1 或 0.5。");
        IsBusy = true;
        try
        {
            var map = new SkuMapping(MappingTenant, SelectedPlatform.Id, MappingShop, MappingProduct, MappingVariant, SelectedSku.Id, multiplier, 0);
            var saved = await api.PutAsync<SkuMapping>("/api/mappings", new MappingUpdate(map, SelectedMapping?.Version ?? 0));
            _bootstrap.Catalog.Mappings.RemoveAll(m => m.TenantId == saved.TenantId && m.Platform == saved.Platform && m.ShopId == saved.ShopId && m.PlatformProductId == saved.PlatformProductId && m.PlatformSkuId == saved.PlatformSkuId);
            _bootstrap.Catalog.Mappings.Add(saved); LoadPlatform();
            SelectedMapping = Mappings.Single(m => m.Source == saved);
            Message = $"SKU 映射已保存为 v{saved.Version}。历史验证报告继续保留原映射快照。";
        }
        finally { IsBusy = false; }
    }
    private async Task LoadSampleCoreAsync()
    {
        if (SelectedSample is null) return;
        var sample = await api.GetAsync<SampleOrder>($"/api/samples/{SelectedSample.Id}");
        InputJson = JsonSerializer.Serialize(sample, ErpJson.Options);
    }
    public async Task LoadSampleAsync()
    {
        IsBusy = true;
        try { await LoadSampleCoreAsync(); Message = "已载入模拟样例，可以编辑 JSON 后校验。"; }
        finally { IsBusy = false; }
    }
    public async Task ValidateAsync()
    {
        IsBusy = true;
        LastReport = null; Issues.Clear(); OutputJson = "正在验证当前输入…";
        ResultTitle = "正在校验"; ResultDetail = "等待服务返回结果。"; ResultColor = "#63758C";
        try
        {
            var report = await api.ValidateAsync(InputJson); ShowReport(report); await RefreshReportsAsync();
            Message = report.Passed ? "模型校验完成并保存报告；尚未进入审单、库存或发货流程。" : "已发现阻断项，修正数据或补充 SKU 映射后可重新校验。";
        }
        finally { IsBusy = false; }
    }
    private void ShowReport(ValidationReport report)
    {
        LastReport = report; Issues.Clear(); foreach (var issue in report.Issues) Issues.Add(issue);
        OutputJson = JsonSerializer.Serialize(report.Order is null ? (object)report : report.Order, ErpJson.Options);
        ResultTitle = report.Passed ? "模型校验通过" : "发现需要修正的问题";
        ResultColor = report.Passed ? "#087E64" : "#B54708";
        ResultDetail = report.Passed
            ? $"{report.Order!.Lines.Count} 条订单明细 · {report.Order.StockRequirements.Count} 种组件需求 · 应付 ¥{report.Order.ExpectedPayable:F2}"
            : $"{report.Issues.Count(i => i.Severity == "Error")} 项阻断 · 无业务订单生成";
        CommandManager.InvalidateRequerySuggested();
    }
    private async Task RefreshReportsAsync()
    {
        Reports.Clear(); foreach (var report in await api.GetAsync<List<ValidationReport>>("/api/reports")) Reports.Add(report);
        SelectedReport = Reports.FirstOrDefault(); Changed(nameof(ReportCount));
    }
    private async Task ExportAsync()
    {
        if (LastReport is null) return;
        var dialog = new SaveFileDialog { Filter = "JSON 验证报告|*.json", FileName = $"erp-validation-{LastReport.ReportId[..8]}.json" };
        if (dialog.ShowDialog() == true)
        {
            await File.WriteAllTextAsync(dialog.FileName, JsonSerializer.Serialize(LastReport, ErpJson.Options));
            Message = "验证报告已导出。";
        }
    }
    public void ShowError(Exception error)
    {
        Message = "操作未完成：" + error.Message;
        if (IsValidation)
        {
            LastReport = null; ResultTitle = "请检查输入或服务状态"; ResultColor = "#B54708";
            ResultDetail = "当前输入未完成校验。"; OutputJson = error.Message;
        }
    }
}
