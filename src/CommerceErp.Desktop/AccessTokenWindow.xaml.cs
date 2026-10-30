using System.Windows;
namespace CommerceErp.Desktop;
public partial class AccessTokenWindow : Window
{
    public AccessTokenWindow(string address) { InitializeComponent(); AddressText.Text = address; Loaded += (_, _) => TokenBox.Focus(); }
    public string Token => TokenBox.Password;
    private void Connect_Click(object sender, RoutedEventArgs e)
    { if (Token.Length < 32) { AddressText.Text = "令牌至少需要 32 字符。"; return; } DialogResult = true; }
}
