namespace CommerceErp.Core;

public enum BillKind { Sale, Refund, PlatformFee, Freight }
public sealed record BillColumns(string Transaction = "流水号", string Order = "订单号", string Kind = "类型", string Amount = "金额", string Time = "发生时间", string Currency = "币种", string AfterSale = "售后编号");
public sealed record BillInput(Platform Platform, string ShopId, string BatchNumber, string FileName, string Content,
    BillColumns Columns, Dictionary<string, BillKind> Types);
public sealed record ImportBillRequest(string OperationId, BillInput Input, string Actor, string Reason);
public sealed record BillPreview(bool Valid, string ContentHash, int NewRows, int DuplicateRows, List<string> Errors, List<BillPreviewLine> Lines);
public sealed record BillPreviewLine(int Line, string Transaction, string Order, BillKind Kind, decimal Amount, DateTimeOffset Time, string AfterSale, string Match);
public sealed record FinanceResult(string Id, long Version, bool Replayed, string Message);
public sealed record FinanceAction(string OperationId, long ExpectedVersion, string Actor, string Reason);
public sealed record BillBatch(string Id, string Number, Platform Platform, string ShopId, string FileName, int NewRows, int DuplicateRows, string State, long Version, string Hash, string CreatedAt);
public sealed record BillRow(string Id, string BatchId, string Transaction, Platform Platform, string ShopId, string ExternalOrderId, BillKind Kind, decimal Amount,
    DateTimeOffset Time, string SourceCase, string? OrderId, string? CaseId, long Version, string State, string Match);
public sealed record BillPage(List<BillRow> Items, int Total, int Page, int PageSize);
public sealed record MatchBillRequest(string OperationId, long ExpectedVersion, string OrderId, string? CaseId, string Evidence, string Actor, string Reason);
public sealed record FinanceHistory(long Id, string EntityId, string Action, string Actor, string Reason, string Data, string CreatedAt);
public sealed record BillDetails(BillRow Row, string OriginalRecord, List<FinanceHistory> History);
public sealed record SetSkuCostRequest(string OperationId, long ExpectedVersion, decimal UnitCost, string Evidence, string Actor, string Reason);
public sealed record SkuCost(string SkuId, string Code, decimal? UnitCost, long Version, string Evidence);
public sealed record CostSnapshot(string ShipmentLineId, string ShipmentNumber, string OrderId, string ExternalOrderId, string SkuId, string SkuCode, decimal Quantity,
    decimal? UnitCost, decimal? Cost, decimal GoodReturned, decimal? ReturnCredit, long Version, string Source);
public sealed record FillCostRequest(string OperationId, long ExpectedVersion, decimal UnitCost, string Evidence, string Actor, string Reason);
public sealed record SetFeePlanRequest(string OperationId, long ExpectedVersion, decimal PlatformFee, decimal Freight, string Evidence, string Actor, string Reason);
public sealed record FeePlan(string OrderId, decimal? PlatformFee, decimal? Freight, long Version, string Evidence);
public sealed record FinanceOrder(string OrderId, string ExternalOrderId, Platform Platform, string ShopId, decimal Paid, decimal Income, decimal ExpectedRefund,
    decimal Refund, decimal PlatformFee, decimal Freight, decimal NetSettlement, decimal? NetCost, decimal? Profit, string ShippingState,
    List<string> Problems, int RowCount, string Fingerprint);
public sealed record FinanceOrderDetails(FinanceOrder Order, FeePlan FeePlan, List<BillRow> Rows, List<CostSnapshot> Costs, List<FinanceHistory> History);
public sealed record FinanceOrderPage(List<FinanceOrder> Items, int Total, int Page, int PageSize);
public sealed record FinanceGroup(string Key, int Orders, int Incomplete, decimal Income, decimal Refund, decimal Fees, decimal? Cost, decimal? Profit);
public sealed record ProductFinance(string SkuCode, int Orders, int Incomplete, decimal Income, decimal Refund, decimal Fees, decimal? Cost, decimal? Profit);
public sealed record FinanceReport(List<FinanceGroup> Shops, List<ProductFinance> Products, int Orders, int Incomplete, int UnmatchedRows);
public sealed record CreateFinanceExportRequest(string OperationId, string ExportNumber, string Target, List<string> OrderIds, string Actor, string Reason);
public sealed record FinanceExport(string Id, string Number, string Target, string State, long Version, string Hash, int Orders, string CreatedAt);
public sealed record FinanceExportDocument(FinanceExport Summary, string Json, string Csv, string CsvHash, bool Stale, List<FinanceHistory> History);
public sealed record FinanceAudit(bool Passed, List<string> Problems);
