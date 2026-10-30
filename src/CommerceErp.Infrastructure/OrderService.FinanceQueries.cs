using CommerceErp.Core;
using static CommerceErp.Infrastructure.CommerceDatabase;

namespace CommerceErp.Infrastructure;

public sealed partial class OrderService
{
    private const string BatchSql="SELECT Id,Number,Platform,ShopId,FileName,NewRows,DuplicateRows,State,Version,ContentHash,CreatedAt FROM BillBatches";
    private static List<BillBatch> BatchesRead(DbSession db,string? id=null)=>db.Query(BatchSql+" WHERE TenantId=$t AND ($id IS NULL OR Id=$id)"+SecurityContext.ShopFilter("BillBatches")+" ORDER BY CreatedAt DESC",r=>new BillBatch(r.GetString(0),r.GetString(1),Enum.Parse<Platform>(r.GetString(2)),r.GetString(3),r.GetString(4),r.GetInt32(5),r.GetInt32(6),r.GetString(7),r.GetInt64(8),r.GetString(9),r.GetString(10)),("t",Tenant(db)),("id",id));
    private static BillBatch BillBatchRead(DbSession db,string id)=>BatchesRead(db,id).SingleOrDefault()??throw new BusinessRuleException("账单批次不存在。");
    public List<BillBatch> BillBatches()=>_database.Read(db=>BatchesRead(db));
    public string OriginalBill(string id)=>_database.Read(db=>{_=BillBatchRead(db,id);return db.Scalar<string>("SELECT RawContent FROM BillBatches WHERE Id=$id",("id",id));});
    private static List<BillRow> BillRowsRead(DbSession db,string? id=null,string? order=null)=>db.Query("""
        SELECT r.Id,r.BatchId,r.TransactionId,r.Platform,r.ShopId,r.ExternalOrderId,r.Kind,r.AmountCents,r.OccurredAt,r.SourceCase,r.OrderId,r.CaseId,r.Version,b.State
        FROM BillRows r JOIN BillBatches b ON b.Id=r.BatchId WHERE r.TenantId=$t AND ($id IS NULL OR r.Id=$id) AND ($o IS NULL OR r.OrderId=$o)
        """+SecurityContext.ShopFilter("r")+" ORDER BY r.OccurredAt DESC,r.Id",r=>new BillRow(r.GetString(0),r.GetString(1),r.GetString(2),Enum.Parse<Platform>(r.GetString(3)),r.GetString(4),r.GetString(5),Enum.Parse<BillKind>(r.GetString(6)),r.GetInt64(7)/100m,DateTimeOffset.Parse(r.GetString(8)),r.GetString(9),r.IsDBNull(10)?null:r.GetString(10),r.IsDBNull(11)?null:r.GetString(11),r.GetInt64(12),r.GetString(13),r.GetString(13)=="Voided"?"批次已作废":r.IsDBNull(10)?"原订单未匹配":r.GetString(6)=="Refund"&&r.IsDBNull(11)?"售后单待匹配":"已关联，待核对金额"),("t",Tenant(db)),("id",id),("o",order));
    private static BillRow BillRowRead(DbSession db,string id)=>BillRowsRead(db,id).SingleOrDefault()??throw new BusinessRuleException("流水不存在。");
    public BillPage BillRows(int page=1,int pageSize=20,string? search=null,bool unmatched=false)
    {
        FinancePageCheck(page,pageSize,search);
        return _database.Read(db=>{var rows=BillRowsRead(db).Where(r=>string.IsNullOrEmpty(search)||r.ExternalOrderId.Contains(search,StringComparison.OrdinalIgnoreCase)||r.Transaction.Contains(search,StringComparison.OrdinalIgnoreCase)).Where(r=>!unmatched||r.State=="Active"&&(r.OrderId is null||r.Kind==BillKind.Refund&&r.CaseId is null)).ToList();return new BillPage(rows.Skip((page-1)*pageSize).Take(pageSize).ToList(),rows.Count,page,pageSize);});
    }
    private static void FinancePageCheck(int page,int size,string? search){if(page<1||page>100000||size<1||size>100||search?.Length>150)throw new BusinessRuleException("分页或搜索参数无效。");}
    public BillDetails BillDetails(string id)=>_database.Read(db=>new BillDetails(BillRowRead(db,id),db.Scalar<string>("SELECT RawJson FROM BillRows WHERE Id=$id",("id",id)),FinanceHistoryRead(db,id)));
    private static FinanceOrder FinanceOrderRead(DbSession db,string id)
    {
        var o=Get(db,id); var rows=BillRowsRead(db,order:id).Where(r=>r.State=="Active").ToList(); var costs=CostsRead(db,id); var plan=FeePlanRead(db,id); var issues=new List<string>();
        decimal Sum(BillKind kind)=>rows.Where(r=>r.Kind==kind).Sum(r=>r.Amount);
        var income=Sum(BillKind.Sale);var refund=Sum(BillKind.Refund);var fee=Sum(BillKind.PlatformFee);var freight=Sum(BillKind.Freight);
        var cases=db.Query("SELECT Id,State,RefundCents FROM AfterSaleCases WHERE OrderId=$o AND State NOT IN('Rejected','Withdrawn')",r=>(Id:r.GetString(0),State:r.GetString(1),Amount:r.GetInt64(2)/100m),("o",id));
        var expectedRefund=cases.Where(c=>c.State is "Approved" or "Closed").Sum(c=>c.Amount);
        if(o.Source.PaymentState!=PaymentState.Paid)issues.Add("订单不是已支付状态");
        if(o.State is not(ReviewState.Approved or ReviewState.Cancelled))issues.Add("订单尚未审核完成");
        if(income!=o.Source.PaidAmount)issues.Add($"收入差异 {income-o.Source.PaidAmount:0.00}（账单收入减订单实付）");
        if(db.Scalar<int>("SELECT COUNT(*) FROM AfterSaleCases WHERE OrderId=$o AND Kind='ReturnRefund' AND State='Approved'",("o",id))>0)issues.Add("退货尚未本地结案，退货成本待确定");
        if(cases.Any(c=>c.State=="Open"))issues.Add("存在待审核售后");
        foreach(var c in cases.Where(c=>c.State is "Approved" or "Closed"))
        {var total=rows.Where(r=>r.Kind==BillKind.Refund&&r.CaseId==c.Id).Sum(r=>r.Amount);if(total!=c.Amount)issues.Add($"售后 {c.Id[..8]} 退款未达或差异 {total-c.Amount:0.00}");}
        if(rows.Any(r=>r.Kind==BillKind.Refund && (r.CaseId is null||!cases.Any(c=>c.Id==r.CaseId&&c.State is "Approved" or "Closed"))))issues.Add("退款流水缺少有效售后关联");
        if(plan.Version==0)issues.Add("应扣平台费及运费待录入（零费用也须确认）");
        else {if(fee!=plan.PlatformFee)issues.Add($"平台费用差异 {fee-plan.PlatformFee:0.00}");if(freight!=plan.Freight)issues.Add($"运费差异 {freight-plan.Freight:0.00}");}
        var originalLines=o.Normalized?.Lines??[];
        var shipped=db.Query("SELECT l.ExternalLineId,SUM(l.QuantityUnits) FROM ShipmentLines l JOIN Shipments s ON s.Id=l.ShipmentId WHERE l.OrderId=$o AND s.State='Shipped' GROUP BY l.ExternalLineId",r=>(Line:r.GetString(0),Qty:r.GetInt64(1)/1000m),("o",id));
        var fully=originalLines.Count>0&&originalLines.All(l=>shipped.Where(s=>s.Line==l.ExternalLineId).Sum(s=>s.Qty)==l.OrderedQuantity);
        var shipping=o.State==ReviewState.Cancelled?"已取消":fully?"全部出库":shipped.Count>0?"部分出库":"未出库";
        if(o.State!=ReviewState.Cancelled&&!fully)issues.Add("履约未完成，成本尚不完整");
        if(o.State==ReviewState.Cancelled&&expectedRefund!=o.Source.PaidAmount)issues.Add("取消订单缺少完整退款申请");
        if(costs.Any(c=>c.Cost is null))issues.Add("缺少出库成本快照");
        var count=db.Scalar<int>("SELECT COUNT(*) FROM ShipmentComponents c JOIN ShipmentLines l ON l.Id=c.ShipmentLineId JOIN Shipments s ON s.Id=l.ShipmentId WHERE l.OrderId=$o AND s.State='Shipped'",("o",id));
        if(count!=costs.Count)issues.Add("出库成本快照数量不一致");
        decimal? netCost=costs.Any(c=>c.Cost is null)||count!=costs.Count?null:costs.Sum(c=>c.Cost!.Value-c.ReturnCredit!.Value);
        var net=income-refund-fee-freight;
        // Only a complete local reconciliation gets a numerical contribution; this is not platform or tax verification.
        decimal? profit=issues.Count==0&&netCost is not null?net-netCost:null;
        var fingerprint=Hash(new{o.Version,o.Source.PaidAmount,rows=rows.OrderBy(r=>r.Id).ToList(),costs,plan,cases,shipping});
        return new(id,o.Source.ExternalOrderId,o.Source.Platform,o.Source.ShopId,o.Source.PaidAmount,income,expectedRefund,refund,fee,freight,net,netCost,profit,shipping,issues,rows.Count,fingerprint);
    }
    private static List<string> FinanceOrderIds(DbSession db)=>db.Query("SELECT Id FROM SalesOrders WHERE TenantId=$t"+SecurityContext.ShopFilter("SalesOrders")+" ORDER BY CreatedAt DESC,Id",r=>r.GetString(0),("t",Tenant(db)));
    public FinanceOrderPage FinanceOrders(int page=1,int pageSize=20,string? search=null,bool differences=false)
    {
        FinancePageCheck(page,pageSize,search);
        return _database.Read(db=>{var rows=FinanceOrderIds(db).Select(id=>FinanceOrderRead(db,id)).Where(r=>(string.IsNullOrEmpty(search)||r.ExternalOrderId.Contains(search,StringComparison.OrdinalIgnoreCase))&&(!differences||r.Problems.Count>0)).ToList();return new FinanceOrderPage(rows.Skip((page-1)*pageSize).Take(pageSize).ToList(),rows.Count,page,pageSize);});
    }
    public FinanceOrderDetails FinanceOrderDetails(string id)=>_database.Read(db=>new FinanceOrderDetails(FinanceOrderRead(db,id),FeePlanRead(db,id),BillRowsRead(db,order:id),CostsRead(db,id),FinanceHistoryRead(db,id)));
    // Largest remainder allocation, keyed by stable sales line id, preserves every cent for product analysis.
    private static Dictionary<string,decimal> AllocateAmount(decimal amount,List<NormalizedLine> lines)
    {
        if(lines.Count==0)return [];
        var weights=lines.Select(l=>(Id:l.ExternalLineId,Weight:Math.Max(0,l.GoodsAmount-l.MerchantDiscount))).ToList();
        if(weights.Sum(x=>x.Weight)==0)weights=lines.Select(l=>(Id:l.ExternalLineId,Weight:l.OrderedQuantity)).ToList();
        var total=weights.Sum(x=>x.Weight); var cents=decimal.ToInt64(amount*100);
        var raw=weights.Select(w=>(w.Id,Raw:Math.Abs(cents)*w.Weight/total)).ToList();
        var allocated=raw.ToDictionary(x=>x.Id,x=>decimal.ToInt64(decimal.Floor(x.Raw))); var remain=Math.Abs(cents)-allocated.Values.Sum();
        foreach(var row in raw.OrderByDescending(x=>x.Raw-decimal.Floor(x.Raw)).ThenBy(x=>x.Id,StringComparer.Ordinal).Take((int)remain))allocated[row.Id]++;
        return allocated.ToDictionary(x=>x.Key,x=>x.Value/100m*Math.Sign(cents));
    }
    public FinanceReport FinanceReport()=>_database.Read(db=>
    {
        var orders=FinanceOrderIds(db).Select(id=>FinanceOrderRead(db,id)).ToList();
        var shops=orders.GroupBy(o=>$"{o.Platform} / {o.ShopId}").Select(g=>new FinanceGroup(g.Key,g.Count(),g.Count(o=>o.Profit is null),g.Sum(o=>o.Income),g.Sum(o=>o.Refund),g.Sum(o=>o.PlatformFee+o.Freight),g.Any(o=>o.NetCost is null)?null:g.Sum(o=>o.NetCost),g.Any(o=>o.Profit is null)?null:g.Sum(o=>o.Profit))).ToList();
        var products=new List<ProductFinance>();
        foreach(var o in orders)
        {
            var lines=Get(db,o.OrderId).Normalized?.Lines??[];var income=AllocateAmount(o.Income,lines);var refunds=AllocateAmount(o.Refund,lines);var fees=AllocateAmount(o.PlatformFee+o.Freight,lines);
            var costs=CostsRead(db,o.OrderId); var lineIds=db.Query("SELECT Id,ExternalLineId FROM ShipmentLines WHERE OrderId=$o",r=>(Id:r.GetString(0),Ext:r.GetString(1)),("o",o.OrderId)).ToDictionary(x=>x.Id,x=>x.Ext);
            foreach(var g in lines.GroupBy(l=>l.InternalSkuCode))
            {
                var ids=g.Select(l=>l.ExternalLineId).ToHashSet();var selected=costs.Where(c=>ids.Contains(lineIds[c.ShipmentLineId])).ToList();
                decimal? cost=selected.Any(c=>c.Cost is null)?null:selected.Sum(c=>c.Cost!.Value-c.ReturnCredit!.Value);
                var inc=g.Sum(l=>income[l.ExternalLineId]);var rf=g.Sum(l=>refunds[l.ExternalLineId]);var fee=g.Sum(l=>fees[l.ExternalLineId]);
                products.Add(new(g.Key,1,o.Profit is null?1:0,inc,rf,fee,cost,o.Profit is null?null:inc-rf-fee-cost));
            }
        }
        var grouped=products.GroupBy(p=>p.SkuCode).Select(g=>new ProductFinance(g.Key,g.Sum(p=>p.Orders),g.Sum(p=>p.Incomplete),g.Sum(p=>p.Income),g.Sum(p=>p.Refund),g.Sum(p=>p.Fees),g.Any(p=>p.Cost is null)?null:g.Sum(p=>p.Cost),g.Any(p=>p.Profit is null)?null:g.Sum(p=>p.Profit))).ToList();
        var unmatched=BillRowsRead(db).Count(r=>r.State=="Active"&&(r.OrderId is null||r.Kind==BillKind.Refund&&r.CaseId is null));
        return new FinanceReport(shops,grouped,orders.Count,orders.Count(o=>o.Profit is null),unmatched);
    });
}
