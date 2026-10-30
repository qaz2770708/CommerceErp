using System.Net.Http;
using System.Net.Http.Json;
using System.Windows;
using CommerceErp.Core;

namespace CommerceErp.Desktop;

public partial class ConnectionWindow : Window
{
    private readonly ClientConnection _original;
    public string SessionToken => TokenBox.Password;
    public ConnectionWindow()
    {
        InitializeComponent(); _original = ClientConnection.Load();
        if (_original.Mode == "Remote")
        {
            ModeBox.SelectedIndex = 1; UrlBox.Text = _original.Url;
            RememberBox.IsChecked = _original.ProtectedToken.Length > 0;
            if (_original.ProtectedToken.Length > 0)
                try { TokenBox.Password = _original.Token(); } catch { StatusText.Text = "保存的令牌无法由当前 Windows 用户解密，请重新填写。"; }
        }
    }
    private async Task<ServiceHealth?> TestAsync()
    {
        if (ModeBox.SelectedIndex == 0) { StatusText.Text = "本地模式：启动时自动开启本机服务，沿用 data 工作区。"; return null; }
        var address = ClientConnection.ValidateAddress(UrlBox.Text);
        if (TokenBox.Password.Length < 32) throw new InvalidOperationException("访问令牌至少需要 32 字符。");
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = address, Timeout = TimeSpan.FromSeconds(10) };
        http.DefaultRequestHeaders.Add("X-Erp-Session", TokenBox.Password);
        using var response = await http.GetAsync("/api/health");
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException("连接失败，状态码 " + (int)response.StatusCode + "。请检查服务地址和令牌。");
        var health = await response.Content.ReadFromJsonAsync<ServiceHealth>(ErpJson.Options) ?? throw new InvalidOperationException("健康检查结果为空。");
        LocalApiClient.ValidateHealth(health, true);
        StatusText.Text = $"连接成功 · {health.DatabaseProvider}\n数据库标识：{health.InstanceId}\n保存后下次启动生效。";
        return health;
    }
    private async void Test_Click(object sender, RoutedEventArgs e) => await RunAsync(false);
    private async void Save_Click(object sender, RoutedEventArgs e) => await RunAsync(true);
    private async Task RunAsync(bool save)
    {
        TestButton.IsEnabled = SaveButton.IsEnabled = false;
        try
        {
            var health = await TestAsync();
            if (!save) return;
            var next = health is null ? ClientConnection.Local : ClientConnection.Remote(UrlBox.Text, TokenBox.Password, RememberBox.IsChecked == true);
            // Reuse a profile only when its endpoint is unchanged; database identity is checked again on every startup/request.
            if (next.Mode == "Remote" && _original.Mode == "Remote" && next.Url == _original.Url && NewProfileBox.IsChecked != true) next = next with { ProfileId = _original.ProfileId };
            next.Save(); DialogResult = true;
        }
        catch (Exception ex) { StatusText.Text = ex switch { System.Security.Cryptography.CryptographicException => "当前 Windows 环境无法加密保存。请取消勾选保存令牌，每次启动输入。设置尚未保存。", HttpRequestException => "连接失败，请检查服务是否运行、网络及 HTTPS 证书。", _ => ex.Message }; }
        finally { TestButton.IsEnabled = SaveButton.IsEnabled = true; }
    }
}
