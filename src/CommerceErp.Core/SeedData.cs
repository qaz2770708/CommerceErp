namespace CommerceErp.Core;

public static class SeedData
{
    public const string Tenant = "demo-enterprise";
    public static readonly List<PlatformInfo> Platforms =
    [
        new(Platform.Taobao, "淘宝 / 天猫", "https://open.taobao.com/", "自研准入需区分天猫与淘宝集市；核实聚石塔、存储和奇门互通边界。"),
        new(Platform.Pinduoduo, "拼多多", "https://open.pinduoduo.com/", "官方正文未完成核实；应用类型、接口权限与数据流转要求均待后台确认。"),
        new(Platform.Jd, "京东", "https://open.jd.com/", "按商家开放平台现行业务类型申请；核实资质与敏感接口云鼎部署要求。"),
        new(Platform.Douyin, "抖店", "https://op.jinritemai.com/", "区分商家自研和软件服务商，核实店铺授权、接口范围与部署要求。")
    ];
    public static string Shop(Platform platform) => platform switch
    {
        Platform.Taobao => "demo-tmall-01", Platform.Pinduoduo => "demo-pdd-01",
        Platform.Jd => "demo-jd-01", _ => "demo-douyin-01"
    };

    public static Dictionary<Platform, List<CheckDefinition>> Checklists() => Platforms.ToDictionary(p => p.Id, p =>
    {
        var reference = p.Id switch
        {
            Platform.Taobao => "https://developer.alibaba.com/docs/doc.htm?articleId=118848&docType=1",
            Platform.Jd => "https://jos.jd.com/faq/list.htm?id=847",
            Platform.Douyin => "https://op.jinritemai.com/docs/guide-docs/133",
            _ => p.Portal
        };
        return new List<CheckDefinition>
        {
            new("identity", "主体与接入身份", "记录企业自用 / 服务商身份、主体及申请路径；不要填写证件号码或密钥。", reference),
            new("store-type", "店铺类型与归属", p.Notice + " 记录适用的店铺类型及同主体 / 报备要求。", reference),
            new("application", "应用资质与审核", "核实软件著作权、产品说明、申请材料及应用审核状态，记录工单或核实日期。", reference),
            new("api-scope", "接口与消息权限", "逐项确认订单查询、增量、消息、商品、库存、发货、售后、账单权限；记录版本与缺项。", p.Portal),
            new("hosting", "部署与网络边界", "核实指定云、数据库、IP 白名单、回调地址、部署区域和网络连通要求。", reference),
            new("data-boundary", "数据处理与流转", "核实隐私字段、存储位置、保留期限、日志脱敏、解密 / 面单权限及获准的数据互通方式。", reference),
            new("authorization", "店铺授权与续期", "记录授权验证结果、失效与续期方式。这里只记结果，不接收 AppSecret 或 Token。", reference),
            new("integration-test", "样例与联调条件", "确认可用测试店铺、脱敏字段样例、金额单位、时间语义、分页窗口、限流和回调重试规则。", p.Portal)
        };
    });

    public static List<PlatformReadiness> Readiness() => Platforms.Select(p => new PlatformReadiness(
        p.Id, 0, Checklists()[p.Id].Select(c => new CheckEntry(c.Key, CheckStatus.NotChecked, "")).ToList())).ToList();

    public static List<InternalSku> Skus() =>
    [
        new("sku-shirt", "TS-WHITE-M", "基础白色 T 恤 · M", true, []),
        new("sku-mug", "MUG-BLUE", "蓝色陶瓷杯 · 350ml", true, []),
        new("sku-kit", "KIT-WELCOME", "迎新礼盒 · T 恤 ×1 / 杯子 ×2", true,
            [new("sku-shirt", 1), new("sku-mug", 2)]),
        new("sku-retired", "OLD-001", "停用商品（校验示例）", false, [])
    ];

    public static List<SkuMapping> Mappings() => Platforms.SelectMany(p => new[]
    {
        new SkuMapping(Tenant, p.Id, Shop(p.Id), "product-100", "variant-shirt", "sku-shirt", 1, 1),
        new SkuMapping(Tenant, p.Id, Shop(p.Id), "product-200", "variant-mug", "sku-mug", 1, 1),
        new SkuMapping(Tenant, p.Id, Shop(p.Id), "product-300", "variant-kit", "sku-kit", 1, 1)
    }).ToList();

    public static SampleOrder BaseOrder(Platform platform) => new()
    {
        SchemaVersion = 1, DataKind = "synthetic", TenantId = Tenant, Platform = platform,
        ShopId = Shop(platform), ExternalOrderId = "000900719925474099312345678901",
        SourceUpdatedAt = DateTimeOffset.Parse("2026-10-28T10:00:00+08:00"),
        PaymentState = PaymentState.Paid, FulfillmentState = FulfillmentState.Unfulfilled,
        Currency = "CNY", ShippingFee = 8m, OrderDiscount = 5m, PaidAmount = 111m,
        Lines =
        [
            new() { ExternalLineId = "line-001", PlatformProductId = "product-100", PlatformSkuId = "variant-shirt", Quantity = 1, GoodsAmount = 89m, MerchantDiscount = 10m },
            new() { ExternalLineId = "line-002", PlatformProductId = "product-200", PlatformSkuId = "variant-mug", Quantity = 1, GoodsAmount = 29m, MerchantDiscount = 0m }
        ]
    };

    public static List<SampleDocument> Samples()
    {
        var douyin = BaseOrder(Platform.Douyin);
        var taobao = BaseOrder(Platform.Taobao) with
        {
            ShippingFee = 0, OrderDiscount = 0, PaidAmount = 258,
            Lines = [new() { ExternalLineId = "kit-line", PlatformProductId = "product-300", PlatformSkuId = "variant-kit", Quantity = 2, GoodsAmount = 258, MerchantDiscount = 0 }]
        };
        var pdd = BaseOrder(Platform.Pinduoduo);
        pdd = pdd with { Lines = [pdd.Lines[0] with { PlatformSkuId = "variant-new" }, pdd.Lines[1]] };
        return
        [
            new(new("douyin-valid", "抖店 · 普通订单", "通过：优惠与运费核对一致"), douyin),
            new(new("taobao-bundle", "淘宝 / 天猫 · 组合商品", "通过：2 份礼盒展开为 T 恤 2 件、杯子 4 件"), taobao),
            new(new("pdd-unmapped", "拼多多 · 缺失 SKU 映射", "阻断：variant-new 尚未关联内部 SKU"), pdd),
            new(new("jd-amount-error", "京东 · 金额差异", "阻断：实付金额相差 0.01 元"), BaseOrder(Platform.Jd) with { PaidAmount = 110.99m }),
            new(new("douyin-zero-quantity", "抖店 · 数量异常", "阻断：商品数量为 0"), douyin with { Lines = [douyin.Lines[0] with { Quantity = 0 }, douyin.Lines[1]] }),
            new(new("jd-long-id", "京东 · 长订单号", "通过：字符串保留前导零及长订单号"), BaseOrder(Platform.Jd))
        ];
    }
}
