using System.Windows;
using System.Windows.Controls;
using CommerceErp.Core;

namespace CommerceErp.Desktop;

public sealed record TaskDisplay(WorkTaskRow Source)
{
    public string Title=>Label(Source.Title);
    public string RelatedNumber=>Source.RelatedNumber;
    public string State=>Label(Source.State);
    public static string Label(string value)=>value switch
    {"OrderAccepted"=>"订单已接入","OrderStateChanged"=>"订单状态变化","OrderApproved"=>"审单完成","OrderCancelled"=>"订单取消","StockReceived"=>"库存收货","StockPolicyChanged"=>"库存策略变更","ShipmentDelivery"=>"发货回传","Pending"=>"等待处理","Processing"=>"处理中","RetryWait"=>"延迟重试","Unknown"=>"结果不明","Blocked"=>"条件未满足","Failed"=>"处理失败","Rejected"=>"平台拒绝","Confirmed"=>"已确认","ManualRetry"=>"人工重新排队","LeaseExpired"=>"中断后接续","StaleLeaseIgnored"=>"过期执行已忽略",_=>value};
}
public sealed record TaskHistoryDisplay(WorkTaskHistory Source){public string Action=>TaskDisplay.Label(Source.Action);}
public partial class WorkTaskWindow:Window
{
    private readonly LocalApiClient _api;private bool _busy,_loading;private int _page=1,_total;private WorkTaskRow? _selected;private string _kind="",_state="",_search="";
    public WorkTaskWindow(LocalApiClient api){InitializeComponent();_api=api;Buttons();}
    private void Buttons(){SearchButton.IsEnabled=!_busy;PreviousButton.IsEnabled=!_busy&&_page>1;NextButton.IsEnabled=!_busy&&_page*20<_total;RetryButton.IsEnabled=!_busy&&_selected?.CanRetry==true&&_api.PendingCount==0;TasksGrid.IsEnabled=!_busy;}
    private async Task Run(Func<Task> action){if(_busy)return;_busy=true;Buttons();try{await action();}catch(Exception e){StatusText.Text="操作未完成："+e.Message+(_api.PendingCount>0?" 请求已保留，请回订单中心使用原请求重试。":"");}finally{_busy=false;Buttons();}}
    public async Task RefreshAsync()
    {
        if(_api.Allows("security.manage"))HealthText.Text=(await _api.GetAsync<TaskHealth>("/api/tasks/health")).Message;
        var summary=await _api.GetAsync<WorkTaskSummary>("/api/tasks/summary");
        SummaryText.Text=$"授权范围总览：共 {summary.Total} 项  ·  等待 {summary.Waiting}  ·  处理中 {summary.Processing}  ·  需关注 {summary.Attention}  ·  已确认 {summary.Confirmed}";
        await LoadPageAsync(_page);StatusText.Text="已刷新。状态可能由后台继续更新；详情不包含原始平台载荷或收件信息。";
    }
    private async Task LoadPageAsync(int page)
    {
        var result=await _api.GetAsync<WorkTaskPage>($"/api/tasks?page={page}&pageSize=20&kind={_kind}&state={_state}&search={Uri.EscapeDataString(_search)}");
        _page=page;_total=result.Total;_selected=null;_loading=true;
        try{TasksGrid.ItemsSource=result.Items.Select(r=>new TaskDisplay(r)).ToList();TasksGrid.SelectedIndex=-1;}finally{_loading=false;}
        IdentityText.Clear();MessageText.Text="选择任务查看处理记录。";ScheduleText.Text="";HistoryGrid.ItemsSource=null;PageText.Text=$"第 {_page} 页 / {_total} 项";Buttons();
    }
    public async Task SearchAsync(){_kind=(KindBox.SelectedItem as ComboBoxItem)?.Tag?.ToString()??"";_state=(StateBox.SelectedItem as ComboBoxItem)?.Tag?.ToString()??"";_search=SearchBox.Text.Trim();_page=1;await RefreshAsync();}
    public async Task OpenSelectedAsync()
    {
        if(TasksGrid.SelectedItem is not TaskDisplay selected)return;
        var detail=await _api.GetAsync<WorkTaskDetails>($"/api/tasks/{selected.Source.Kind}/{selected.Source.Id}");
        if(TasksGrid.SelectedItem is not TaskDisplay active||active.Source.Id!=selected.Source.Id||active.Source.Kind!=selected.Source.Kind)return;
        _selected=detail.Task;
        var visible=TasksGrid.Items.Cast<TaskDisplay>().ToList();var index=TasksGrid.SelectedIndex;visible[index]=new TaskDisplay(detail.Task);_loading=true;
        try{TasksGrid.ItemsSource=visible;TasksGrid.SelectedIndex=index;}finally{_loading=false;}
        IdentityText.Text=$"{TaskDisplay.Label(_selected.Title)}\n任务号：{_selected.Id}\n关联：{_selected.RelatedNumber}";
        MessageText.Text=_selected.Message;ScheduleText.Text=$"当前：{TaskDisplay.Label(_selected.State)} · 已尝试 {_selected.Attempts} 次\n最近更新（UTC）：{_selected.UpdatedAt}\n下次处理（UTC）：{_selected.NextAttemptAt??"未安排"}";
        HistoryGrid.ItemsSource=detail.History.Select(h=>new TaskHistoryDisplay(h)).ToList();Buttons();
    }
    public async Task RetrySelectedAsync()
    {
        if(_selected is null||!_selected.CanRetry)throw new BusinessRuleException("请先选择允许重试的任务。");
        if(_api.PendingCount>0)throw new BusinessRuleException("请先核清原有未确认请求。");
        var r=await _api.PostCommandAsync<WorkTaskResult>($"/api/tasks/{_selected.Kind}/{_selected.Id}/retry",new{expectedVersion=_selected.Version,actor="当前账号",reason=ReasonBox.Text});
        await OpenSelectedAsync();StatusText.Text=r.Message;ReasonBox.Clear();Buttons();
    }
    private async void Selected_Changed(object sender,SelectionChangedEventArgs e){if(!_loading)await Run(OpenSelectedAsync);}
    private async void Search_Click(object sender,RoutedEventArgs e)=>await Run(SearchAsync);
    private async void Previous_Click(object sender,RoutedEventArgs e)=>await Run(()=>LoadPageAsync(_page-1));
    private async void Next_Click(object sender,RoutedEventArgs e)=>await Run(()=>LoadPageAsync(_page+1));
    private async void Retry_Click(object sender,RoutedEventArgs e)=>await Run(RetrySelectedAsync);
}
