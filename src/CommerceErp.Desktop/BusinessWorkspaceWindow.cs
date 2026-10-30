using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
namespace CommerceErp.Desktop;
public sealed class BusinessWorkspaceWindow:Window
{
    public TextBox Enterprise{get;}=new(){Margin=new Thickness(0,6,0,14),MaxLength=100};public TextBox Warehouse{get;}=new(){Text="主仓",Margin=new Thickness(0,6,0,14),MaxLength=100};public string EnterpriseName=>Enterprise.Text.Trim();public string WarehouseName=>Warehouse.Text.Trim();
    public BusinessWorkspaceWindow(string directory)
    {
        Title="建立正式工作区";Width=550;Height=380;WindowStartupLocation=WindowStartupLocation.CenterScreen;ResizeMode=ResizeMode.NoResize;var panel=new StackPanel{Margin=new Thickness(24)};
        panel.Children.Add(new TextBlock{Text="建立正式资料工作区",FontSize=24,FontWeight=FontWeights.SemiBold});panel.Children.Add(new TextBlock{Text="工作区初始无商品和库存，平台尚未连接。",Margin=new Thickness(0,10,0,14)});panel.Children.Add(new TextBlock{Text="企业名称"});panel.Children.Add(Enterprise);panel.Children.Add(new TextBlock{Text="仓库名称"});panel.Children.Add(Warehouse);var status=new TextBlock{TextWrapping=TextWrapping.Wrap};var button=new Button{Content="建立工作区",Margin=new Thickness(0,0,0,12)};button.Click+=(_,_)=>{if(EnterpriseName.Length==0||WarehouseName.Length==0){status.Text="请填写企业及仓库名称。";return;}DialogResult=true;};panel.Children.Add(button);panel.Children.Add(status);Content=panel;
    }
    public static async Task InitializeAsync(string directory,string name,string warehouse)
    {
        var tool=Path.Combine(AppContext.BaseDirectory,"tools","CommerceErp.DatabaseTool.exe");if(!File.Exists(tool))throw new InvalidOperationException("缺少数据库工具，请先运行 build.ps1。");
        var psi=new ProcessStartInfo(tool){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};foreach(var arg in new[]{"initialize-business","--data",directory,"--name",name,"--warehouse-name",warehouse})psi.ArgumentList.Add(arg);
        using var p=Process.Start(psi)!;var stdout=p.StandardOutput.ReadToEndAsync();var stderr=p.StandardError.ReadToEndAsync();await p.WaitForExitAsync();var output=await stdout;var error=await stderr;if(p.ExitCode!=0)throw new InvalidOperationException("正式工作区未建立："+(string.IsNullOrWhiteSpace(error)?output:error));
    }
}
