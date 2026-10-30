using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CommerceErp.Core;

public interface IOrderNormalizer
{
    ValidationReport Validate(SampleOrder order, Catalog catalog,bool allowManual=false);
}

public sealed class OrderNormalizer : IOrderNormalizer
{
    public ValidationReport Validate(SampleOrder order, Catalog catalog,bool allowManual=false)
    {
        var issues = new List<ValidationIssue>();
        void Error(string code, string field, string text) => issues.Add(new("Error", code, field, text));
        void Id(string? value, string field)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 100 || value != value.Trim() || value.Any(char.IsControl))
                Error("INVALID_ID", field, "标识不能为空、包含控制字符或首尾空白，长度不超过 100。标识必须使用字符串。");
        }
        void Money(decimal value, string field)
        {
            if (value < 0 || value > 1_000_000_000m || decimal.Round(value, 2) != value)
                Error("INVALID_MONEY", field, "金额必须非负、至多两位小数，且不超过 10 亿元；此样例使用人民币元。");
        }
        if (order.SchemaVersion != 1) Error("SCHEMA_VERSION", "schemaVersion", "仅支持样例契约版本 1。");
        if (order.DataKind != "synthetic" && !(allowManual&&order.DataKind=="manual")) Error("SYNTHETIC_ONLY", "dataKind", "本阶段只接受 synthetic 模拟数据，不接入真实平台载荷。");
        if (!Enum.IsDefined(order.Platform)) Error("INVALID_PLATFORM", "platform", "不支持的平台。");
        if (!Enum.IsDefined(order.PaymentState) || !Enum.IsDefined(order.FulfillmentState))
            Error("INVALID_STATE", "state", "支付或履约状态不受支持。");
        Id(order.TenantId, "tenantId"); Id(order.ShopId, "shopId"); Id(order.ExternalOrderId, "externalOrderId");
        if (order.SourceUpdatedAt == default) Error("INVALID_TIME", "sourceUpdatedAt", "必须提供包含时区偏移的来源更新时间。");
        if (order.Currency != "CNY") Error("CURRENCY_NOT_SUPPORTED", "currency", "第一步仅验证 CNY；其他币种需要独立币种与精度策略。");
        Money(order.ShippingFee, "shippingFee"); Money(order.OrderDiscount, "orderDiscount"); Money(order.PaidAmount, "paidAmount");
        if (order.Lines is null || order.Lines.Count is < 1 or > 1000)
        {
            Error("INVALID_LINES", "lines", "订单必须包含 1 至 1000 条明细。");
            return Report(order, issues, null);
        }

        var normalized = new List<NormalizedLine>();
        var lineIds = new HashSet<string>(StringComparer.Ordinal);
        decimal goods = 0, discount = 0;
        for (var i = 0; i < order.Lines.Count; i++)
        {
            var line = order.Lines[i];
            var field = $"lines[{i}]";
            if (line is null) { Error("INVALID_LINE", field, "明细不能为 null。"); continue; }
            Id(line.ExternalLineId, field + ".externalLineId");
            Id(line.PlatformProductId, field + ".platformProductId"); Id(line.PlatformSkuId, field + ".platformSkuId");
            if (!lineIds.Add(line.ExternalLineId ?? "")) Error("DUPLICATE_LINE", field, "外部订单行 ID 重复。");
            if (line.Quantity <= 0 || line.Quantity > 1_000_000 || decimal.Round(line.Quantity, 3) != line.Quantity)
                Error("INVALID_QUANTITY", field + ".quantity", "数量必须大于 0、最多三位小数，且不超过 100 万。");
            Money(line.GoodsAmount, field + ".goodsAmount"); Money(line.MerchantDiscount, field + ".merchantDiscount");
            if (line.MerchantDiscount > line.GoodsAmount) Error("EXCESS_DISCOUNT", field, "行优惠不能超过行商品金额。");
            // Clamp invalid input before aggregating to keep error reporting safe from decimal overflow.
            goods += Math.Clamp(line.GoodsAmount, 0, 1_000_000_000m);
            discount += Math.Clamp(line.MerchantDiscount, 0, 1_000_000_000m);
            var matches = catalog.Mappings.Where(m => m.TenantId == order.TenantId && m.Platform == order.Platform &&
                m.ShopId == order.ShopId && m.Enabled && m.PlatformProductId == line.PlatformProductId && m.PlatformSkuId == line.PlatformSkuId).ToList();
            if (matches.Count == 0) { Error("SKU_NOT_MAPPED", field, $"平台 SKU {line.PlatformSkuId} 尚未映射；请按租户、平台及店铺建立映射。"); continue; }
            if (matches.Count != 1) { Error("AMBIGUOUS_MAPPING", field, "存在多条有效映射，需修复唯一性。"); continue; }
            var mapping = matches[0];
            var sku = catalog.Skus.SingleOrDefault(s => s.Id == mapping.InternalSkuId);
            if (sku is null || !sku.Enabled) { Error("SKU_UNAVAILABLE", field, "内部 SKU 不存在或已停用。"); continue; }
            if (mapping.UnitMultiplier <= 0 || mapping.UnitMultiplier > 1000 || decimal.Round(mapping.UnitMultiplier, 3) != mapping.UnitMultiplier)
            { Error("INVALID_MULTIPLIER", field, "单位换算倍率必须为正，最多三位小数且不超过 1000。"); continue; }
            if (line.Quantity <= 0 || line.Quantity > 1_000_000) continue;
            var quantity = line.Quantity * mapping.UnitMultiplier;
            var requirements = new List<StockRequirement>();
            if (sku.Components.Count == 0) requirements.Add(new(sku.Id, sku.Code, quantity));
            else
            {
                foreach (var component in sku.Components)
                {
                    var child = catalog.Skus.SingleOrDefault(s => s.Id == component.SkuId);
                    if (child is null || !child.Enabled || child.Components.Count != 0 || component.Quantity <= 0 || component.Quantity > 1000 || decimal.Round(component.Quantity, 3) != component.Quantity)
                    { Error("INVALID_BUNDLE", field, "组合商品仅支持启用的单层组件；组件数量须为正且最多三位小数。"); continue; }
                    requirements.Add(new(child.Id, child.Code, quantity * component.Quantity));
                }
            }
            if (requirements.Any(r => decimal.Round(r.Quantity, 3) != r.Quantity))
                Error("STOCK_QUANTITY_PRECISION", field, "换算后的库存数量超过三位小数，请调整单位或倍率。");
            normalized.Add(new(line.ExternalLineId!, line.PlatformProductId, line.PlatformSkuId, sku.Id, sku.Code,
                line.Quantity, mapping.UnitMultiplier, mapping.Version, line.GoodsAmount, line.MerchantDiscount, requirements));
        }
        var payable = goods - discount + Math.Clamp(order.ShippingFee, 0, 1_000_000_000m) - Math.Clamp(order.OrderDiscount, 0, 1_000_000_000m);
        if (payable < 0) Error("NEGATIVE_PAYABLE", "orderDiscount", "订单优惠超过应付金额。");
        if (order.PaymentState == PaymentState.Paid && payable != order.PaidAmount)
            Error("PAYMENT_MISMATCH", "paidAmount", $"应付 {payable:F2} 元，实付 {order.PaidAmount:F2} 元；差额 {order.PaidAmount - payable:F2} 元。");
        if (order.PaymentState == PaymentState.Unpaid && order.PaidAmount != 0)
            Error("UNPAID_WITH_PAYMENT", "paidAmount", "未支付订单的本样例实付字段必须为 0。");
        if (order.PaymentState != PaymentState.Paid || order.FulfillmentState != FulfillmentState.Unfulfilled)
            issues.Add(new("Warning", "FULFILLMENT_HOLD", "state", "非已付款待履约订单；模型通过也不代表允许发货。退款流水需在后续售后模块处理。"));

        NormalizedOrder? result = null;
        if (issues.All(x => x.Severity != "Error"))
        {
            var businessKey = JsonSerializer.Serialize(new[] { order.TenantId, order.Platform.ToString(), order.ShopId, order.ExternalOrderId });
            var total = normalized.SelectMany(l => l.StockRequirements).GroupBy(r => r.InternalSkuId)
                .Select(g => new StockRequirement(g.Key, g.First().SkuCode, g.Sum(x => x.Quantity))).ToList();
            result = new(businessKey, order.TenantId, order.Platform, order.ShopId, order.ExternalOrderId,
                order.SourceUpdatedAt, order.PaymentState, order.FulfillmentState, order.Currency, goods, discount,
                order.ShippingFee, order.OrderDiscount, payable, order.PaidAmount, normalized, total);
        }
        return Report(order, issues, result);
    }

    private static ValidationReport Report(SampleOrder order, List<ValidationIssue> issues, NormalizedOrder? result) =>
        new(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(order, ErpJson.Options)))),
            issues.All(i => i.Severity != "Error"), issues, result);
}
