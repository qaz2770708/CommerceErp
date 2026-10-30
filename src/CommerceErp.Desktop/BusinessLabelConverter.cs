using System.Globalization;
using System.Windows.Data;
namespace CommerceErp.Desktop;
public sealed class BusinessLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value?.ToString() switch
    {
        "Administrator" => "管理员", "CustomerService" => "客服", "Warehouse" => "仓储", "Finance" => "财务", "Observer" => "观察员",
        "ReturnGood" => "退货良品", "AfterSaleRelease" => "售后释放", "AfterSaleVoid" => "售后作废",
        "Active" => "有效预占", "Released" => "已释放", "Consumed" => "已出库消耗", "Opening" => "期初", "Receive" => "补货", "Ship" => "出库",
        "Draft" => "待拣货", "Picking" => "拣货复核", "Packed" => "已封箱", "Shipped" => "已出库", "Voided" => "已作废", "Superseded" => "已拆合替代",
        "Blocked" => "连接器未配置", "Pending" => "待回传", "Processing" => "回传中", "Unknown" => "结果待回查", "Rejected" => "平台拒绝", "Confirmed" => "平台已确认",
        "Unshipped" => "未出库", "Partial" => "部分出库", "Complete" => "全部出库",
        "Create" => "生成出库单", "Split" => "拆单", "Merge" => "合单", "StartPicking" => "开始拣货", "Scan" => "扫码复核", "ResetVerification" => "重置复核",
        "Pack" => "封箱", "Waybill" => "登记运单", "Dispatch" => "确认出库", "Void" => "作废出库单", "RetryDelivery" => "重试回传",
        "Reserve" => "预占", "Release" => "释放", "Policy" => "库存策略", _ => value?.ToString() ?? ""
    };
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
