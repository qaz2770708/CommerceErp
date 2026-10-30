using System.Windows;
using System.Windows.Controls;
using CommerceErp.Core;
namespace CommerceErp.Desktop;
public sealed class PasswordWindow : Window
{
    public PasswordWindow(LocalApiClient api)
    {
        Title="修改登录密码";Width=440;Height=360;ResizeMode=ResizeMode.NoResize;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var panel=new StackPanel{Margin=new Thickness(28)};Content=panel;
        panel.Children.Add(new TextBlock{Text="当前密码"});var current=new PasswordBox{Margin=new Thickness(0,8,0,16)};panel.Children.Add(current);
        panel.Children.Add(new TextBlock{Text="新密码（12–128 个字符）"});var next=new PasswordBox{Margin=new Thickness(0,8,0,16)};panel.Children.Add(next);
        var status=new TextBlock{Text="保存后退出工作区，请用新密码重新登录。",TextWrapping=TextWrapping.Wrap};panel.Children.Add(status);
        var save=new Button{Content="保存并退出",Margin=new Thickness(0,18,0,0)};panel.Children.Add(save);
        save.Click+=async(_,_)=>{save.IsEnabled=false;try {await api.PostAsync<System.Text.Json.JsonElement>("/api/security/password",new ChangePasswordRequest(current.Password,next.Password));current.Clear();next.Clear();try{await api.LogoutAsync();}catch{}Close();Application.Current.MainWindow.Close();}catch(Exception e){status.Text=e.Message;}finally{save.IsEnabled=true;}};
    }
}
