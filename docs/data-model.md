> 版本说明：下文保留早期阶段设计背景。6.4 已加入账号范围、正式空白工作区与主数据；当前权限、任务策略和边界以 [README](../README.md)、[账号说明](accounts-security.md)、[事务说明](business-transactions.md) 为准。真实平台与外部财务仍未连接。正式人工订单使用 DataKind=manual，演示订单仍为 synthetic。

# 模拟订单数据口径（沿用第一阶段契约）

> 版本说明：本文描述原阶段业务设计。6.2 的认证方式、角色和当前范围以 [账号说明](accounts-security.md) 为准，后续边界以 [开发路线](next-stage.md) 为准。
当前日期基准：2026-10-28。以下模型属于本工程自己的模拟契约，不是淘宝、拼多多、京东或抖店的官方请求 / 响应结构。真实平台 DTO 必须在获得接口权限和字段样例后，单独建立适配层。

## 订单输入

| 字段 | 约束与含义 |
|---|---|
| schemaVersion | 固定为 1 |
| dataKind | 固定为 synthetic；不用于证明数据脱敏，只用于防止误当作真实接入 |
| tenantId / shopId / externalOrderId | 字符串；1–100 字符，不允许首尾空白或控制字符 |
| platform | Taobao / Pinduoduo / Jd / Douyin |
| sourceUpdatedAt | ISO 8601，必须包含 Z 或明确的时区偏移；保留 DateTimeOffset |
| paymentState | Unpaid / Paid / Refunded |
| fulfillmentState | Unfulfilled / Shipped / Cancelled；与支付状态独立 |
| currency | 本阶段只支持 CNY |
| shippingFee | 订单运费，人民币元；0–10 亿元，最多两位小数 |
| orderDiscount | 订单级优惠，不与行优惠重复计算 |
| paidAmount | 原始已支付金额，非结算净额；退款现金流下一阶段另建模型 |
| lines | 1–1000 条，externalLineId 在单内唯一 |
| lines.quantity | 数量大于零，不超过 100 万，最多三位小数 |
| lines.goodsAmount | **整行**优惠前商品金额，不是单价 |
| lines.merchantDiscount | 整行商家优惠，不能超过 goodsAmount |

默认普通样例：`118 - 10 + 8 - 5 = 111.00 元`。Paid 订单的实付必须精确等于该应付；Unpaid 必须实付为 0。非 Paid/Unfulfilled 组合会附加履约警告；退款状态不在此处自动冲销库存或资金。

金额使用 `decimal`；不使用浮点容差，也不静默四舍五入错误的输入。平台券、平台补贴、分期支付、定金尾款等未纳入当前模拟公式，真实适配时必须根据平台金额语义扩展，不能强行套用本示例。

## SKU 与组件

映射唯一键为 `(TenantId, Platform, ShopId, PlatformProductId, PlatformSkuId)`。匹配使用区分大小写的精确字符串；缺映射、重复映射、停用内部 SKU 均阻断。修改映射使用 ExpectedVersion，保存后版本递增，旧版本写入返回冲突。

普通商品需求量 = 平台订单数量 × UnitMultiplier。组合商品再乘以组件用量，最终按内部实物 SKU 汇总。倍率和组件用量最多三位小数、不超过 1000；展开后的数量超过三位小数则阻断，不静默舍入。

每份输出明细包含原平台标识、内部 SKU、映射版本、倍率、金额和展开组件快照。历史验证报告不会随映射变更而重写。模型验证页的组件需求只是计算结果；第二阶段订单中心会将通过审单的组件需求转为库存预占，见 `business-transactions.md`。

## 输出与业务标识

NormalizedOrder 保留来源租户、平台、店铺、订单 ID。BusinessKey 是上述四元组的 JSON 数组字符串，避免简单分隔符拼接的歧义；第二阶段数据库已对四个原始字段建立复合唯一约束。

ValidationReport 包含随机 ReportId、UTC 生成时间、标准序列化输入的 SHA-256、Passed、Issues、Order。出现 Error 时不生成 NormalizedOrder；Warning 不影响结构验证通过。每次运行生成独立报告，这是重复验证记录，不是订单接入幂等机制。

## 本地 HTTP 接口

所有接口均需要 `X-Erp-Session` 请求头。令牌由桌面端生成，经子进程环境变量交给服务，仅在内存中持有；API 绑定回环地址随机端口。正文上限 256 KB，JSON 深度上限 32，未知字段拒绝。

| 方法 / 路径 | 用途 |
|---|---|
| GET /api/health | 本地服务健康与模拟模式 |
| GET /api/bootstrap | 清单定义、核实记录、目录、映射、样例索引 |
| PUT /api/readiness/{platform} | 按 ExpectedRevision 保存平台核实记录 |
| PUT /api/mappings | 按 ExpectedVersion 保存映射 |
| GET /api/samples/{id} | 获取内置模拟订单 |
| POST /api/validate | 运行模型验证并保存报告 |
| GET /api/reports | 最近 200 份报告，按新到旧排列 |
| POST /api/shutdown | 桌面端关闭所启动的本地服务 |

格式错误返回 400、无会话凭证 401、未知样例 404、版本冲突 409、过大请求 413。合法 JSON 的业务校验失败仍返回 200，并在报告中使用 `passed=false` 表达；客户端必须检查 Passed。

本地会话接口仅供同机原型使用，不能开放给生产用户或作为商家权限隔离依据。生产接口需正式身份认证、服务端租户上下文、RBAC、审计、HTTPS、独立密钥管理和平台数据边界设计。
