using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;

namespace CommerceErp.Desktop;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel) { InitializeComponent(); DataContext = viewModel; }
    private void OpenReference(object sender, RequestNavigateEventArgs e)
    {
        try { if (e.Uri.Scheme == "https") Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception error) { ((MainViewModel)DataContext).ShowError(error); }
        e.Handled = true;
    }
}
