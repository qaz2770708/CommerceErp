using System.Text.Json;
using System.Text.Json.Serialization;

namespace CommerceErp.Core;

public enum Platform { Taobao, Pinduoduo, Jd, Douyin }
public enum CheckStatus { NotChecked, InProgress, Verified, Blocked }
public enum PaymentState { Unpaid, Paid, Refunded }
public enum FulfillmentState { Unfulfilled, Shipped, Cancelled }

public static class ErpJson
{
    public static JsonSerializerOptions CreateOptions() => new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Create(System.Text.Unicode.UnicodeRanges.All),
        MaxDepth = 32,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false), new ExplicitOffsetDateTimeConverter() }
    };
    public static readonly JsonSerializerOptions Options = CreateOptions();
}

public sealed class ExplicitOffsetDateTimeConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        var value = reader.GetString();
        if (value is null || !System.Text.RegularExpressions.Regex.IsMatch(value, @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d{1,7})?(Z|[+-]\d{2}:\d{2})$") ||
            !DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var result))
            throw new JsonException("时间必须为带 Z 或显式时区偏移的 ISO 8601 格式。");
        return result;
    }
    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) => writer.WriteStringValue(value);
}

public sealed record PlatformInfo(Platform Id, string Name, string Portal, string Notice);
public sealed record CheckDefinition(string Key, string Title, string Guidance, string ReferenceUrl);
public sealed record CheckEntry(string Key, CheckStatus Status, string Evidence);
public sealed record PlatformReadiness(Platform Platform, int Revision, List<CheckEntry> Items);
public sealed record ReadinessUpdate(int ExpectedRevision, List<CheckEntry> Items);
public sealed record SkuComponent(string SkuId, decimal Quantity);
public sealed record InternalSku(string Id, string Code, string Name, bool Enabled, List<SkuComponent> Components);
public sealed record SkuMapping(
    string TenantId, Platform Platform, string ShopId, string PlatformProductId,
    string PlatformSkuId, string InternalSkuId, decimal UnitMultiplier, int Version, bool Enabled=true);
public sealed record MappingUpdate(SkuMapping Mapping, int ExpectedVersion);
public sealed record Catalog(List<InternalSku> Skus, List<SkuMapping> Mappings);

// This deliberately small fixture contract is NOT any platform's official API response.
// Real connector DTOs must stay separate and be built against approved API versions.
public sealed record SampleOrder
{
    public required int SchemaVersion { get; init; }
    public required string DataKind { get; init; }
    public required string TenantId { get; init; }
    public required Platform Platform { get; init; }
    public required string ShopId { get; init; }
    public required string ExternalOrderId { get; init; }
    public required DateTimeOffset SourceUpdatedAt { get; init; }
    public required PaymentState PaymentState { get; init; }
    public required FulfillmentState FulfillmentState { get; init; }
    public required string Currency { get; init; }
    public required decimal ShippingFee { get; init; }
    public required decimal OrderDiscount { get; init; }
    public required decimal PaidAmount { get; init; }
    public required List<SampleOrderLine> Lines { get; init; }
}

public sealed record SampleOrderLine
{
    public required string ExternalLineId { get; init; }
    public required string PlatformProductId { get; init; }
    public required string PlatformSkuId { get; init; }
    public required decimal Quantity { get; init; }
    // Line totals before/after merchant discount; not unit prices. Amounts in CNY yuan.
    public required decimal GoodsAmount { get; init; }
    public required decimal MerchantDiscount { get; init; }
}

public sealed record StockRequirement(string InternalSkuId, string SkuCode, decimal Quantity);
public sealed record NormalizedLine(
    string ExternalLineId, string PlatformProductId, string PlatformSkuId,
    string InternalSkuId, string InternalSkuCode, decimal OrderedQuantity,
    decimal UnitMultiplier, int MappingVersion, decimal GoodsAmount,
    decimal MerchantDiscount, List<StockRequirement> StockRequirements);
public sealed record NormalizedOrder(
    string BusinessKey, string TenantId, Platform Platform, string ShopId, string ExternalOrderId,
    DateTimeOffset SourceUpdatedAt, PaymentState PaymentState, FulfillmentState FulfillmentState,
    string Currency, decimal GoodsAmount, decimal MerchantDiscount, decimal ShippingFee,
    decimal OrderDiscount, decimal ExpectedPayable, decimal PaidAmount,
    List<NormalizedLine> Lines, List<StockRequirement> StockRequirements);
public sealed record ValidationIssue(string Severity, string Code, string Field, string Message);
public sealed record ValidationReport(
    string ReportId, DateTimeOffset CreatedAt, string InputHash, bool Passed,
    List<ValidationIssue> Issues, NormalizedOrder? Order);
public sealed record SampleDescriptor(string Id, string Title, string ExpectedResult);
public sealed record SampleDocument(SampleDescriptor Descriptor, SampleOrder Order);
public sealed record BootstrapData(
    string Mode, bool LiveConnectionsEnabled, string ReferenceDate,
    List<PlatformInfo> Platforms, Dictionary<Platform, List<CheckDefinition>> Checklists,
    List<PlatformReadiness> Readiness, Catalog Catalog, List<SampleDescriptor> Samples,WorkspaceProfile? Workspace=null);

public sealed class BusinessRuleException(string message) : Exception(message);
public sealed class ConflictException(string message) : Exception(message);
