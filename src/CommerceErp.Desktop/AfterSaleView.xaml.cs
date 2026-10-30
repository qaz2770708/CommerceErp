using System.Globalization;
using System.Windows.Controls;
using System.Windows.Data;
namespace CommerceErp.Desktop;
public partial class AfterSaleView : UserControl { public AfterSaleView() => InitializeComponent(); }
public sealed class AfterSaleLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value?.ToString() switch
    { "Apply" => "申请并拦截", "Approve" => "批准售后", "Reject" => "驳回申请", "Withdraw" => "撤回申请", "InspectReturn" => "质检入库", "RecordRefund" => "登记退款跟踪", "CloseLocal" => "人工凭证结案", _ => value?.ToString() ?? "" };
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
