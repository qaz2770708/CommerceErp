using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommerceErp.Core;

namespace CommerceErp.Desktop;

public partial class App : Application
{
    private LocalApiClient? _api;
    private bool _closing;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var masterVerify=e.Args.Contains("--verify-master-ui");
        var verifyIndex = Array.IndexOf(e.Args, masterVerify?"--verify-master-ui":"--verify-ui");
        var verifyDirectory = verifyIndex >= 0 && e.Args.Length > verifyIndex + 1 ? Path.GetFullPath(e.Args[verifyIndex + 1]) : null;
        try
        {
            string? sessionToken = null;
            if (verifyDirectory is null && e.Args.Contains("--configure"))
            { var connectionWindow = new ConnectionWindow(); if (connectionWindow.ShowDialog() != true) { Shutdown(); return; } sessionToken = connectionWindow.SessionToken; }
            _api = new();
            if (verifyDirectory is not null && !masterVerify)
            {
                var data = Path.Combine(verifyDirectory, "data"); Directory.CreateDirectory(data);
                if (File.Exists(Path.Combine(data, "commerce.db"))) throw new InvalidOperationException("UI 验证需要全新的输出目录，避免修改已有数据。");
                var pending = new Dictionary<string, LocalApiClient.PendingCommand>
                {
                    ["verification-pending"] = new("/api/inventory/sku-mug/receive", JsonSerializer.Serialize(new ReceiveStockRequest(Guid.NewGuid().ToString("N"), 7, "UI 验证：恢复未完成补货请求"), ErpJson.Options))
                };
                await File.WriteAllTextAsync(Path.Combine(data, "pending-commands.json"), JsonSerializer.Serialize(pending, ErpJson.Options));
            }
            if(masterVerify&&verifyDirectory is not null)await BusinessWorkspaceWindow.InitializeAsync(Path.Combine(verifyDirectory,"data"),"正式工作区界面测试企业","测试主仓");
            if (verifyDirectory is not null) await _api.StartAsync(Path.Combine(verifyDirectory, "data"));
            else if(e.Args.Contains("--business"))
            {
                var data=Path.Combine(AppContext.BaseDirectory,"business-data");
                if(!File.Exists(Path.Combine(data,"commerce.db"))){var setup=new BusinessWorkspaceWindow(data);if(setup.ShowDialog()!=true){Shutdown();return;}await BusinessWorkspaceWindow.InitializeAsync(data,setup.EnterpriseName,setup.WarehouseName);}
                await _api.StartAsync(data);
            }
            else
            {
                var configuration = ClientConnection.Load();
                if (configuration.Mode == "Remote" && sessionToken is null)
                {
                    if (configuration.ProtectedToken.Length > 0)
                        try { sessionToken = configuration.Token(); } catch (System.Security.Cryptography.CryptographicException) { }
                    if (sessionToken is null)
                    { var prompt = new AccessTokenWindow(configuration.Url); if (prompt.ShowDialog() != true) { Shutdown(); return; } sessionToken = prompt.Token; }
                }
                await _api.StartConfiguredAsync(configuration, sessionToken);
            }
            if (verifyDirectory is not null)
            {
                await _api.PostAsync<AccountInfo>("/api/security/setup",new SetupAccountRequest("ui-admin","界面验证管理员","UiFixture-Only-2026!"));
                await _api.LoginAsync("ui-admin","UiFixture-Only-2026!");
            }
            else
            {
                var status = await _api.GetAsync<SecurityStatus>("/api/security/status");
                var loginWindow = new LoginWindow(_api,status.SetupRequired);
                if(loginWindow.ShowDialog()!=true) { await _api.DisposeAsync(); Shutdown(); return; }
            }
            var vm = new MainViewModel(_api);
            var window = new MainWindow(vm);
            MainWindow = window;
            if (verifyDirectory is not null) { window.WindowStartupLocation = WindowStartupLocation.Manual; window.Left = -20000; window.Top = -20000; window.ShowActivated = false; }
            window.Closing += async (_, args) =>
            {
                if (_closing) return;
                args.Cancel = true; _closing = true;
                if (_api is not null) await _api.DisposeAsync();
                window.Close(); Shutdown();
            };
            window.Show();
            await vm.InitializeAsync();
            if (verifyDirectory is not null)
            {
                if(masterVerify)await VerifyMasterUiAsync(window,vm,_api,verifyDirectory);else await VerifyUiAsync(window, vm, verifyDirectory);
                _closing = true; await _api.DisposeAsync(); window.Close(); Shutdown(0);
            }
        }
        catch (Exception error)
        {
            if (verifyDirectory is not null)
            { Directory.CreateDirectory(verifyDirectory); await File.WriteAllTextAsync(Path.Combine(verifyDirectory, "ui-error.txt"), error.ToString()); }
            else MessageBox.Show(error.Message, "ERP 启动未完成", MessageBoxButton.OK, MessageBoxImage.Error);
            if (_api is not null) await _api.DisposeAsync();
            Shutdown(1);
        }
    }

    // In-process integration/render test of this application, with synthetic data only.
    private async Task VerifyUiAsync(MainWindow window, MainViewModel vm, string directory)
    {
        Directory.CreateDirectory(directory);
        var bindingErrors = new StringWriter();
        var listener = new TextWriterTraceListener(bindingErrors);
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        async Task Capture(string name)
        {
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
            var root = (FrameworkElement)window.Content;
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(root);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(directory, name + ".png")); encoder.Save(stream);
        }
        var ops = vm.Operations;
        vm.Navigate("orders"); await Capture("01-order-import");
        await ops.RetryAsync();
        if (ops.Inventory.Single(i => i.SkuId == "sku-mug").OnHand != 107) throw new InvalidOperationException("未完成补货请求恢复失败。");
        await ops.ImportAsync();
        if (ops.Details?.Summary.State != ReviewState.Approved) throw new InvalidOperationException("默认订单应审核成功。");
        var firstId = ops.Details.Summary.Id;
        await ops.ImportAsync();
        if (ops.Dashboard?.TotalOrders != 1 || ops.Details?.Summary.Id != firstId) throw new InvalidOperationException("重复接入不应生成第二张订单。");
        await Capture("02-order-approved");
        vm.Navigate("inventory"); await Capture("03-inventory-reserved");
        ops.Reason = "UI 验证：模拟取消"; await ops.CancelAsync();
        if (ops.Inventory.Any(i => i.Reserved != 0)) throw new InvalidOperationException("取消应释放全部预占。");
        vm.Navigate("orders"); await Capture("04-order-cancelled");
        ops.SelectedSample = ops.Samples.Single(s => s.Id == "jd-amount-error"); await ops.NewSampleAsync(); await ops.ImportAsync();
        if (ops.Details?.Summary.State != ReviewState.Exception) throw new InvalidOperationException("金额错误应进入异常。");
        await Capture("05-order-exception");
        var corrected = JsonSerializer.Deserialize<SampleOrder>(ops.CorrectionJson, ErpJson.Options)! with { PaidAmount = 111 };
        ops.CorrectionJson = JsonSerializer.Serialize(corrected, ErpJson.Options); ops.Reason = "UI 验证：修正金额"; await ops.CorrectAsync(); await ops.ReviewAsync();
        if (ops.Details?.Summary.State != ReviewState.Approved) throw new InvalidOperationException("更正并重审后应审核成功。");
        ops.SelectedStock = ops.Inventory.Single(i => i.SkuId == "sku-shirt"); ops.ReceiveQuantity = "10.125"; ops.StockReason = "UI 验证：补货"; await ops.ReceiveAsync();
        if (ops.Inventory.Single(i => i.SkuId == "sku-shirt").OnHand != 110.125m) throw new InvalidOperationException("补货余额不正确。");
        ops.Frozen = "2"; ops.Safety = "3"; await ops.PolicyAsync(); await ops.CheckAsync();
        vm.Navigate("inventory"); await Capture("06-inventory-ledger");
        vm.Navigate("readiness"); await Capture("07-readiness");
        vm.Navigate("mappings"); await Capture("02-mappings");
        vm.Navigate("validation"); await vm.ValidateAsync();
        if (vm.LastReport?.Passed != true) throw new InvalidOperationException("默认样例应通过校验。");
        await Capture("03-validation-pass");
        vm.SelectedSample = vm.Samples.Single(s => s.Id == "pdd-unmapped"); await vm.LoadSampleAsync(); await vm.ValidateAsync();
        if (vm.LastReport?.Issues.Any(i => i.Code == "SKU_NOT_MAPPED") != true) throw new InvalidOperationException("缺失映射应被阻断。");
        await Capture("04-validation-blocked");
        vm.SelectedPlatform = vm.Platforms.Single(p => p.Id == Platform.Pinduoduo);
        vm.MappingProduct = "product-100"; vm.MappingVariant = "variant-new";
        vm.SelectedSku = vm.Skus.Single(s => s.Id == "sku-shirt"); await vm.SaveMappingAsync(); await vm.ValidateAsync();
        if (vm.LastReport?.Passed != true) throw new InvalidOperationException("补全映射后应通过校验。");
        vm.SelectedPlatform = vm.Platforms.Single(p => p.Id == Platform.Douyin);
        vm.Checks[0].Status = CheckStatus.InProgress; vm.Checks[0].Evidence = "UI 自动验证：仅模拟编辑与保存，不表示真实资质核实。";
        await vm.SaveReadinessAsync();
        if (vm.Checks[0].Status != CheckStatus.InProgress) throw new InvalidOperationException("接入记录未保存。");
        window.Width = 1120; window.Height = 760; vm.Navigate("orders"); await Capture("08-orders-minimum-size");
        vm.Navigate("inventory"); await Capture("09-inventory-minimum-size");
        await VerifyWarehouseAsync(window, vm, directory, Capture);
        await VerifyAfterSalesAsync(window, vm, _api!, Capture);
        await VerifyFinanceAsync(window, vm, _api!, directory, Capture);
        async Task CaptureDialog(Window dialog, string name)
        {
            if(!dialog.IsVisible) {dialog.Owner = window; dialog.WindowStartupLocation = WindowStartupLocation.Manual;}
            dialog.Left = -20000; dialog.Top = -20000; dialog.ShowActivated = false; if (!dialog.IsVisible) dialog.Show();
            await dialog.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); dialog.UpdateLayout();
            var content = (FrameworkElement)dialog.Content;
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            var drawing = new DrawingVisual();
            using (var context = drawing.RenderOpen()) context.DrawRectangle(new VisualBrush(content), null, new Rect(0,0,content.ActualWidth,content.ActualHeight));
            bitmap.Render(drawing); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(directory, name + ".png")); png.Save(stream);
        }
        var settings = new ConnectionWindow(); settings.ModeBox.SelectedIndex = 1;
        if (settings.RememberBox.IsChecked == true) throw new InvalidOperationException("访问令牌不应默认持久保存。");
        await CaptureDialog(settings, "42-service-connection");
        settings.UrlBox.Text = "http://192.0.2.10:5088";
        settings.TestButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        await settings.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        if (!settings.StatusText.Text.Contains("HTTPS")) throw new InvalidOperationException("连接设置应阻止远程明文传输。");
        await CaptureDialog(settings, "43-connection-rejected"); settings.Close();
        var login = new AccessTokenWindow("https://erp.example.test:5088"); await CaptureDialog(login, "44-session-token"); login.Close();
        var accounts = new AccountWindow(_api!); await accounts.RefreshAsync();
        accounts.UsernameBox.Text="ui-customer";accounts.DisplayBox.Text="界面客服";accounts.PasswordBox.Password="UiFixture-Temporary!";accounts.ReasonBox.Text="界面验证：建立客服";
        await accounts.SaveAsync();
        if(!accounts.UsersGrid.Items.Cast<AccountInfo>().Any(u=>u.Username=="ui-customer"))throw new InvalidOperationException("账号窗口保存未生效："+accounts.StatusText.Text);
        await CaptureDialog(accounts,"45-account-management");accounts.AccountTabs.SelectedIndex=1;await CaptureDialog(accounts,"46-access-audit");
        accounts.AuditActorBox.Text="ui-admin";await accounts.SearchAuditAsync();
        if(accounts.AuditGrid.Items.Cast<SecurityAudit>().Any(a=>a.ActorName!="ui-admin"))throw new InvalidOperationException("审计筛选未生效。");
        accounts.AuditActionBox.Text=new string('x',151);var invalidAuditRejected=false;
        try{await accounts.SearchAuditAsync();}catch(BusinessRuleException){invalidAuditRejected=true;}
        if(!invalidAuditRejected)throw new InvalidOperationException("非法审计条件未被拒绝。");
        // A failed search must not replace the successful query used by export.
        accounts.AuditReasonBox.Text="界面验证：筛选后导出审计";await accounts.ExportAuditToAsync(Path.Combine(directory,"audit-export.json"));
        accounts.AuditActionBox.Clear();await CaptureDialog(accounts,"51-audit-filter-export");accounts.Close();
        var tasks=new WorkTaskWindow(_api!);tasks.KindBox.SelectedIndex=1;tasks.StateBox.SelectedIndex=5;await tasks.SearchAsync();
        if(tasks.TasksGrid.Items.Count==0)throw new InvalidOperationException("任务页未显示被阻断的业务事件。");
        tasks.TasksGrid.SelectedIndex=0;await tasks.OpenSelectedAsync();
        await CaptureDialog(tasks,"52-task-center");
        var taskRow=((TaskDisplay)tasks.TasksGrid.SelectedItem).Source;
        var inventoryBefore=JsonSerializer.Serialize(await _api!.GetAsync<List<InventoryRow>>("/api/inventory"),ErpJson.Options);
        var emptyReasonRejected=false;try{await tasks.RetrySelectedAsync();}catch(BusinessRuleException){emptyReasonRejected=true;}
        if(!emptyReasonRejected||_api.PendingCount!=0)throw new InvalidOperationException("空原因必须拒绝且不遗留待确认请求。");
        tasks.ReasonBox.Text="界面验证：核查任务状态后重新排队";await tasks.RetrySelectedAsync();
        var taskDetail=await _api.GetAsync<WorkTaskDetails>($"/api/tasks/{taskRow.Kind}/{taskRow.Id}");
        if(taskDetail.Task.Id!=taskRow.Id||!taskDetail.History.Any(h=>h.Action=="ManualRetry"&&h.Actor.StartsWith("ui-admin (",StringComparison.Ordinal)))throw new InvalidOperationException("任务重试须保留原任务号并记录真实账号。");
        if(inventoryBefore!=JsonSerializer.Serialize(await _api.GetAsync<List<InventoryRow>>("/api/inventory"),ErpJson.Options))throw new InvalidOperationException("任务重试不应变更库存。");
        await CaptureDialog(tasks,"53-task-retry");tasks.Width=tasks.MinWidth;tasks.Height=tasks.MinHeight;await CaptureDialog(tasks,"54-task-minimum");tasks.Close();
        var setupPreview=new LoginWindow(_api!,true);await CaptureDialog(setupPreview,"47-first-administrator");setupPreview.Close();
        await _api!.LogoutAsync();
        Exception? loginError=null;
        var accountLogin=new LoginWindow(_api,false){Owner=window,WindowStartupLocation=WindowStartupLocation.Manual,Left=-20000,Top=-20000,ShowActivated=false};
        accountLogin.Loaded+=async(_,_)=>
        {
            try
            {
                accountLogin.UsernameBox.Text="ui-customer";accountLogin.PasswordBox.Password="incorrect-password";await accountLogin.SubmitAsync();
                if(_api.CurrentAccount is not null||!accountLogin.StatusText.Text.Contains("账号或密码"))throw new InvalidOperationException("错误密码应停留在登录窗口。");
                await CaptureDialog(accountLogin,"48-login-denied");
                accountLogin.PasswordBox.Password="UiFixture-Temporary!";await accountLogin.SubmitAsync();
                if(accountLogin.NewPasswordBox.Visibility!=Visibility.Visible)throw new InvalidOperationException("临时密码应进入改密步骤。");
                await CaptureDialog(accountLogin,"49-forced-password-change");
                accountLogin.NewPasswordBox.Password="UiFixture-Customer-New!";await accountLogin.SubmitAsync();
                if(_api.CurrentAccount?.MustChangePassword!=false)throw new InvalidOperationException("改密未完成。");
            }
            catch(Exception error){loginError=error;accountLogin.Close();}
        };
        if(accountLogin.ShowDialog()!=true||loginError is not null)throw new InvalidOperationException("登录界面闭环未通过",loginError);
        var limitedVm=new MainViewModel(_api);await limitedVm.InitializeAsync();
        if(limitedVm.InventoryPageCommand.CanExecute(null)||limitedVm.FinancePageCommand.CanExecute(null)||limitedVm.AccountsCommand.CanExecute(null)||limitedVm.AfterSales.RefundCommand.CanExecute(null))throw new InvalidOperationException("客服界面应禁用越权入口。");
        window.DataContext=limitedVm;await Capture("50-customer-workspace");
        await _api.LogoutAsync();await _api.LoginAsync("ui-admin","UiFixture-Only-2026!");window.DataContext=vm;
        listener.Flush();
        if (!string.IsNullOrWhiteSpace(bindingErrors.ToString())) throw new InvalidOperationException("WPF 绑定错误：" + bindingErrors);
        PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);
        await File.WriteAllTextAsync(Path.Combine(directory, "ui-result.json"), JsonSerializer.Serialize(new
        {
            passed = true, checks = new[] { "八个业务页面及仓储售后各四个页签渲染", "正常订单校验", "缺失映射阻断", "补映射后通过", "核实记录保存", "最小窗口尺寸渲染", "未发现 WPF 绑定错误", "未完成补货请求恢复", "接入并自动审单", "重复接单去重", "取消释放预占", "金额错误进入异常", "更正后重新审单", "小数补货入账", "冻结与安全库存策略", "库存一致性检查", "收货资料保存及配单", "出库单拆分保留关联", "出库单合并", "实物条码登记", "错扫拦截", "扫码数量复核", "拣货单导出及输入转义", "封箱运单与出库扣账", "平台回传明确阻断并可排队重试", "仓储最小尺寸渲染", "出库后库存与履约一致性", "按原出库行创建售后", "批准后质检与按钮状态", "良品和残次品分开入账", "退款结果不明保留额度", "人工凭证来源确认", "退款历史不可覆盖", "本地结案阻止再次收货", "仓储显示售后拦截", "拣货取消须确认实物归还", "取消作废包裹并释放预占", "售后账实一致性", "售后三页最小窗口渲染", "库存页显示隔离数量", "账单预览及修改后重新校验", "跨日多笔流水导入", "缺失成本不显示零利润", "历史成本补录及冻结", "未来成本不追改历史", "费用核对和退款贡献", "人工匹配保留原文及差异", "作废批次退出汇总", "财务导出本地状态", "JSON和CSV哈希验证", "财务操作不改变库存", "财务结构核对", "财务五页最小窗口渲染", "服务连接设置窗口渲染", "默认不保存访问令牌", "连接设置阻止远端明文HTTP", "单次令牌窗口渲染", "账号窗口创建客服并保存", "账号和访问审计渲染", "首次管理员窗口渲染", "错误密码停留登录界面", "首次改密完整登录流程", "客服界面权限入口禁用", "审计按账号筛选", "筛选快照导出与摘要校验", "查询失败后仍导出先前成功的筛选结果", "任务筛选和详情渲染", "任务重试空原因被拒绝", "任务原标识和真实操作人记录", "任务重试不改变库存", "任务页最小尺寸渲染" }
        }, ErpJson.Options));
    }
}
