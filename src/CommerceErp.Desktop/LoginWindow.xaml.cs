using System.Windows;
using CommerceErp.Core;
namespace CommerceErp.Desktop;
public partial class LoginWindow : Window
{
    private readonly LocalApiClient _api; private bool _setup; private bool _change;
    public LoginWindow(LocalApiClient api,bool setup)
    {
        InitializeComponent();_api=api;_setup=setup;ConnectionText.Text=api.ConnectionLabel;
        Heading.Text=setup?"建立首个管理员":"登录工作区";SubmitButton.Content=setup?"建立并登录":"登录";
        DisplayBox.Visibility=DisplayLabel.Visibility=setup?Visibility.Visible:Visibility.Collapsed;
        Loaded+=(_,_)=>UsernameBox.Focus();
    }
    private async void Submit_Click(object sender,RoutedEventArgs e)=>await SubmitAsync();
    public async Task SubmitAsync()
    {
        SubmitButton.IsEnabled=false;
        try
        {
            if(_change)
            {
                await _api.PostAsync<System.Text.Json.JsonElement>("/api/security/password",new ChangePasswordRequest(PasswordBox.Password,NewPasswordBox.Password));
                try { await _api.LogoutAsync(); } catch { }PasswordBox.Password=NewPasswordBox.Password;NewPasswordBox.Clear();_change=false;
            }
            if(_setup){await _api.PostAsync<AccountInfo>("/api/security/setup",new SetupAccountRequest(UsernameBox.Text,DisplayBox.Text,PasswordBox.Password));_setup=false;}
            var user=await _api.LoginAsync(UsernameBox.Text,PasswordBox.Password);
            if(user.Account.MustChangePassword)
            {
                _change=true;UsernameBox.IsReadOnly=true;NewPasswordBox.Visibility=NewPasswordLabel.Visibility=Visibility.Visible;
                StatusText.Text="管理员设置的是临时密码，请修改后再进入业务工作区。";SubmitButton.Content="修改密码并登录";NewPasswordBox.Focus();return;
            }
            PasswordBox.Clear();NewPasswordBox.Clear();DialogResult=true;
        }
        catch(Exception error){StatusText.Text=error.Message;}
        finally{SubmitButton.IsEnabled=true;}
    }
}
