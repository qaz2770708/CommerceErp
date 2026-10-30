using System.Windows;
using System.Windows.Controls;
using CommerceErp.Core;
namespace CommerceErp.Desktop;
public partial class AccountWindow : Window
{
    private readonly LocalApiClient _api;private AccountInfo? _selected;private bool _busy;private WorkspaceProfile? _workspace;
    public AccountWindow(LocalApiClient api)
    {
        InitializeComponent();_api=api;RoleBox.ItemsSource=Enum.GetValues<AccountRole>();RoleBox.SelectedItem=AccountRole.CustomerService;

    }
    public async Task RefreshAsync(){_workspace=await _api.GetAsync<WorkspaceProfile>("/api/workspace");ShopsPanel.Children.Clear();foreach(var shop in _workspace.Shops)ShopsPanel.Children.Add(new CheckBox{Content=shop.Name,Tag=shop.Id,IsChecked=true,Margin=new Thickness(0,3,0,3)});UsersGrid.ItemsSource=await _api.GetAsync<List<AccountInfo>>("/api/security/accounts");await SearchAuditAsync();}
    private AuditQuery _auditQuery=new();
    private long _auditTotal;
    private bool _auditLoaded;
    private static string? AuditDate(DateTime? date,bool end)=>date is null?null:new DateTimeOffset(end?date.Value.Date.AddDays(1).AddTicks(-1):date.Value.Date).ToUniversalTime().ToString("O");
    public Task SearchAuditAsync()
    {
        return LoadAuditAsync(new(1,50,AuditActorBox.Text,AuditActionBox.Text,AuditOutcomeBox.SelectedIndex switch{1=>"Committed",2=>"Denied",_=>""},AuditResourceBox.Text,AuditDate(AuditFromBox.SelectedDate,false),AuditDate(AuditToBox.SelectedDate,true)));
    }
    private async Task LoadAuditAsync(AuditQuery q)
    {
        var url=$"/api/security/audit/query?page={q.Page}&pageSize={q.PageSize}&actor={Uri.EscapeDataString(q.Actor)}&action={Uri.EscapeDataString(q.Action)}&outcome={q.Outcome}&resource={Uri.EscapeDataString(q.Resource)}";
        if(q.From is not null)url+="&from="+Uri.EscapeDataString(q.From);if(q.To is not null)url+="&to="+Uri.EscapeDataString(q.To);if(q.SnapshotId is not null)url+="&snapshotId="+q.SnapshotId;
        var page=await _api.GetAsync<AuditPage>(url);_auditQuery=q with{SnapshotId=page.SnapshotId};_auditTotal=page.Total;AuditGrid.ItemsSource=page.Items;_auditLoaded=true;
        AuditPrevious.IsEnabled=q.Page>1;AuditNext.IsEnabled=q.Page*q.PageSize<page.Total;AuditPageText.Text=$"第 {q.Page} 页 / 共 {page.Total} 条 · 已固定查询结果；新记录请点刷新";
    }
    private async void SearchAudit_Click(object sender,RoutedEventArgs e)=>await Run(SearchAuditAsync);
    private async void PreviousAudit_Click(object sender,RoutedEventArgs e)=>await Run(async()=>{if(_auditQuery.Page>1)await LoadAuditAsync(_auditQuery with{Page=_auditQuery.Page-1});});
    private async void NextAudit_Click(object sender,RoutedEventArgs e)=>await Run(async()=>{if(_auditQuery.Page*_auditQuery.PageSize<_auditTotal)await LoadAuditAsync(_auditQuery with{Page=_auditQuery.Page+1});});
    public async Task ExportAuditToAsync(string path)
    {
        if(!_auditLoaded)throw new InvalidOperationException("请先成功查询审计记录。");
        var export=await _api.PostAsync<AuditExportDocument>("/api/security/audit/export",new AuditExportRequest(_auditQuery,AuditReasonBox.Text));
        var bytes=System.Text.Encoding.UTF8.GetBytes(export.Json);
        if(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant()!=export.Sha256)throw new System.IO.InvalidDataException("审计导出摘要不符，未保存。");
        await System.IO.File.WriteAllBytesAsync(path,bytes);await System.IO.File.WriteAllTextAsync(path+".sha256",export.Sha256);
        StatusText.Text=$"已导出筛选范围内 {export.Rows} 条记录，并保存 SHA-256 校验文件；导出动作已留痕。";
    }
    private async void ExportAudit_Click(object sender,RoutedEventArgs e)=>await Run(async()=>{var dialog=new Microsoft.Win32.SaveFileDialog{Filter="审计 JSON|*.json",FileName="audit-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+".json"};if(dialog.ShowDialog()==true)await ExportAuditToAsync(dialog.FileName);});
    private async void Refresh_Click(object sender,RoutedEventArgs e)=>await Run(RefreshAsync);
    private void New_Click(object sender,RoutedEventArgs e){UsersGrid.SelectedItem=null;_selected=null;UsernameBox.IsReadOnly=false;UsernameBox.Clear();DisplayBox.Clear();PasswordBox.Clear();ReasonBox.Clear();EnabledBox.IsChecked=true;RoleBox.SelectedItem=AccountRole.CustomerService;}
    private void Selected_Changed(object sender,SelectionChangedEventArgs e)
    {
        if(UsersGrid.SelectedItem is not AccountInfo user)return;_selected=user;UsernameBox.Text=user.Username;UsernameBox.IsReadOnly=true;DisplayBox.Text=user.DisplayName;RoleBox.SelectedItem=user.Role;EnabledBox.IsChecked=user.Enabled;PasswordBox.Clear();
        foreach(CheckBox shop in ShopsPanel.Children)shop.IsChecked=user.Shops.Contains((string)shop.Tag);
    }
    private async void Save_Click(object sender,RoutedEventArgs e)=>await SaveAsync();
    public async Task SaveAsync()=>await Run(async()=>
    {
        var request=new SaveAccountRequest(UsernameBox.Text,DisplayBox.Text,(AccountRole)RoleBox.SelectedItem,_workspace!.TenantId,EnabledBox.IsChecked==true,_selected?.Version??0,
            ShopsPanel.Children.Cast<CheckBox>().Where(c=>c.IsChecked==true).Select(c=>(string)c.Tag).ToList(),[_workspace.WarehouseId],ReasonBox.Text,_selected is null?PasswordBox.Password:null);
        var result=_selected is null?await _api.PostAsync<AccountInfo>("/api/security/accounts",request):await _api.PutAsync<AccountInfo>("/api/security/accounts/"+_selected.Id,request);
        PasswordBox.Clear();StatusText.Text="已保存 "+result.Username+"。旧会话已失效；新建账号首次登录需要改密。";await RefreshAsync();UsersGrid.SelectedItem=UsersGrid.Items.Cast<AccountInfo>().FirstOrDefault(a=>a.Id==result.Id);
    });
    private async void Reset_Click(object sender,RoutedEventArgs e)=>await Run(async()=>
    {
        if(_selected is null)throw new InvalidOperationException("先选择账号。");
        await _api.PostAsync<System.Text.Json.JsonElement>("/api/security/accounts/"+_selected.Id+"/password",new ResetPasswordRequest(_selected.Version,PasswordBox.Password,ReasonBox.Text));
        PasswordBox.Clear();StatusText.Text="密码已重置。该账号下次登录必须修改临时密码。";await RefreshAsync();
    });
    private async Task Run(Func<Task> action){if(_busy)return;_busy=true;SaveButton.IsEnabled=ResetButton.IsEnabled=false;try{await action();}catch(Exception e){StatusText.Text=e.Message;}finally{_busy=false;SaveButton.IsEnabled=ResetButton.IsEnabled=true;}}
}
