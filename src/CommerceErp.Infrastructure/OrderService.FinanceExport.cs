using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CommerceErp.Core;
using static CommerceErp.Infrastructure.CommerceDatabase;

namespace CommerceErp.Infrastructure;

public sealed partial class OrderService
{
    private static string FinanceTextHash(string text)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    private static List<FinanceExport> ExportsRead(DbSession db,string? id=null)=>db.Query("SELECT e.Id,e.Number,e.Target,e.State,e.Version,e.ContentHash,(SELECT COUNT(*) FROM FinanceExportOrders o WHERE o.ExportId=e.Id),e.CreatedAt FROM FinanceExports e WHERE ($id IS NULL OR e.Id=$id)"+DataAccessScope.ExportsFilter("e")+" ORDER BY e.CreatedAt DESC",
        r=>new FinanceExport(r.GetString(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetInt64(4),r.GetString(5),r.GetInt32(6),r.GetString(7)),("id",id));
    public List<FinanceExport> FinanceExports()=>_database.Read(db=>ExportsRead(db));
    public FinanceResult CreateFinanceExport(CreateFinanceExportRequest r)=>FinanceCommand(r.OperationId,"CreateFinanceExport",r,r.Actor,r.Reason,db=>
    {
        BillCsv.Identifier(r.ExportNumber); AfterText(r.Target,"目标财务系统或接收方",150);
        if(r.OrderIds is null||r.OrderIds.Count is <1 or >100||r.OrderIds.Distinct().Count()!=r.OrderIds.Count)throw new BusinessRuleException("请选择 1–100 张不重复的订单。");
        var requestHash=Hash(new{r.Target,orders=r.OrderIds.Order(StringComparer.Ordinal).ToList()});
        var old=db.Query("SELECT Id,RequestHash,Version FROM FinanceExports WHERE Number=$n",x=>(Id:x.GetString(0),Hash:x.GetString(1),Version:x.GetInt64(2)),("n",r.ExportNumber)).SingleOrDefault();
        if(old.Id is not null){if(old.Hash!=requestHash)throw new ConflictException("导出编号已用于不同订单或接收方。");return new(old.Id,old.Version,true,"原导出快照已存在；重复下载不会重新生成。");}
        var orders=r.OrderIds.Select(id=>FinanceOrderRead(db,id)).OrderBy(o=>o.OrderId,StringComparer.Ordinal).ToList();
        if(orders.Any(o=>o.Profit is null))throw new ConflictException("存在未对平、未完成履约或缺少成本/费用的订单，不能生成完整财务导出。");
        foreach(var o in orders)
            if(db.Scalar<int>("SELECT COUNT(*) FROM FinanceExportOrders WHERE OrderId=$o",("o",o.OrderId))>0)throw new ConflictException("订单已有财务导出，请重复下载原包；更正与冲销接口尚未配置，不重复生成入账包。");
        var id=Guid.NewGuid().ToString("N"); var at=Now();
        var rows=orders.SelectMany(o=>BillRowsRead(db,order:o.OrderId).Where(b=>b.State=="Active")).OrderBy(b=>b.Id,StringComparer.Ordinal).ToList();
        var costs=orders.SelectMany(o=>CostsRead(db,o.OrderId)).ToList();
        var json=Json(new{schema="commerce-finance-export/v1",exportId=id,exportNumber=r.ExportNumber,createdAt=at,target=r.Target,currency="CNY",source="ManualBillImport_Unverified",purpose="基础结算贡献分析及财务交接，不是总账凭证或已记账回执",businessKey=id,orders,transactions=rows,costs});
        var csv=new StringBuilder("导出编号,内部订单号,平台订单号,平台,店铺,账单收入,账单退款,平台费,运费,结算净额,商品净成本,基础结算贡献\r\n");
        foreach(var o in orders)
        {
            var text=new[]{r.ExportNumber,o.OrderId,o.ExternalOrderId,o.Platform.ToString(),o.ShopId}.Select(BillCsv.Escape);
            var numbers=new[]{o.Income,o.Refund,o.PlatformFee,o.Freight,o.NetSettlement,o.NetCost!.Value,o.Profit!.Value}.Select(v=>v.ToString("0.00",CultureInfo.InvariantCulture));
            csv.AppendLine(string.Join(',',text.Concat(numbers)));
        }
        var hash=FinanceTextHash(json);
        db.Execute("INSERT INTO FinanceExports VALUES($id,$n,$t,$request,$hash,$json,$csv,$csvHash,'Prepared',1,$now)",("id",id),("n",r.ExportNumber),("t",r.Target),("request",requestHash),("hash",hash),("json",json),("csv",csv.ToString()),("csvHash",FinanceTextHash(csv.ToString())),("now",at));
        foreach(var o in orders)db.Execute("INSERT INTO FinanceExportOrders VALUES($e,$o,$f)",("e",id),("o",o.OrderId),("f",o.Fingerprint));
        foreach(var row in rows)db.Execute("INSERT INTO FinanceExportRows VALUES($e,$r)",("e",id),("r",row.Id));
        FinanceHistoryAdd(db,id,"PrepareExport",r.Actor,r.Reason,new{r.Target,orders=r.OrderIds,hash}); _fault?.Invoke("finance-after-export");
        return new(id,1,false,"财务交接快照已生成，请下载保存；未发送至财务系统，未记账。");
    });
    private static bool ExportStale(DbSession db,string id)=>db.Query("SELECT OrderId,Fingerprint FROM FinanceExportOrders WHERE ExportId=$id",r=>(Order:r.GetString(0),Hash:r.GetString(1)),("id",id)).Any(o=>FinanceOrderRead(db,o.Order).Fingerprint!=o.Hash);
    public FinanceExportDocument FinanceExportDocument(string id)=>_database.Read(db=>
    {
        var summary=ExportsRead(db,id).SingleOrDefault()??throw new BusinessRuleException("导出记录不存在。");
        var content=db.Query("SELECT JsonContent,CsvContent,CsvHash FROM FinanceExports WHERE Id=$id",r=>(Json:r.GetString(0),Csv:r.GetString(1),CsvHash:r.GetString(2)),("id",id)).Single();
        return new FinanceExportDocument(summary,content.Json,content.Csv,content.CsvHash,ExportStale(db,id),FinanceHistoryRead(db,id));
    });
    public FinanceResult MarkFinanceExported(string id,FinanceAction r)=>FinanceCommand(r.OperationId,"MarkFinanceExported",new{id,r},r.Actor,r.Reason,db=>
    {
        var current=ExportsRead(db,id).SingleOrDefault()??throw new BusinessRuleException("导出记录不存在。");
        if(current.Version!=r.ExpectedVersion)throw new ConflictException("导出版本已变化。");
        if(ExportStale(db,id))throw new ConflictException("原业务已变化，此快照只能作为历史记录下载，请先处理差异。");
        if(current.State=="Exported")return new(id,current.Version,true,"已记录本地导出，不代表外部接收或记账。");
        db.Execute("UPDATE FinanceExports SET State='Exported',Version=Version+1 WHERE Id=$id",("id",id)); FinanceHistoryAdd(db,id,"LocalFileSaved",r.Actor,r.Reason,new{});
        return new(id,current.Version+1,false,"本地文件导出已登记；财务接收、记账和冲销均未确认。");
    });
    public FinanceAudit AuditFinance()=>_database.Read(AuditFinanceCore);
    private sealed record BillMapping(BillColumns Columns,Dictionary<string,BillKind> Types);
    private static FinanceAudit AuditFinanceCore(DbSession db)
    {
        var problems=new List<string>();
        foreach(var b in BatchesRead(db))
        {
            if(FinanceTextHash(db.Scalar<string>("SELECT RawContent FROM BillBatches WHERE Id=$id",("id",b.Id)))!=b.Hash)problems.Add("账单原文哈希不一致："+b.Number);
            if(db.Scalar<int>("SELECT COUNT(*) FROM BillRows WHERE BatchId=$id",("id",b.Id))!=b.NewRows)problems.Add("账单明细数量不一致："+b.Number);
            try
            {
                var original=db.Query("SELECT RawContent,MappingJson FROM BillBatches WHERE Id=$id",r=>(Raw:r.GetString(0),Map:r.GetString(1)),("id",b.Id)).Single();
                var map=FromJson<BillMapping>(original.Map);
                var parsed=BillCsv.Parse(new(b.Platform,b.ShopId,b.Number,b.FileName,original.Raw,map.Columns,map.Types)).GroupBy(p=>p.Transaction).ToDictionary(g=>g.Key,g=>g.First());
                foreach(var row in BillRowsRead(db).Where(r=>r.BatchId==b.Id))
                {
                    var stored=db.Query("SELECT SourceHash,RawJson FROM BillRows WHERE Id=$id",r=>(Hash:r.GetString(0),Raw:r.GetString(1)),("id",row.Id)).Single();
                    var current=new BillCsv.Parsed(0,row.Transaction,row.ExternalOrderId,row.Kind,checked((long)(row.Amount*100)),row.Time,row.SourceCase,stored.Raw);
                    if(!parsed.TryGetValue(row.Transaction,out var source)||RowHash(source)!=stored.Hash||RowHash(current)!=stored.Hash||source.Raw!=stored.Raw)problems.Add("账单明细与原文不一致："+row.Transaction);
                }
            }
            catch(Exception e) when(e is BusinessRuleException or System.Text.Json.JsonException or InvalidDataException){problems.Add("账单原文或映射已损坏："+b.Number);}
        }
        if(db.Scalar<int>("SELECT COUNT(*) FROM BillRows r JOIN SalesOrders o ON o.Id=r.OrderId WHERE r.Platform<>o.Platform OR r.ShopId<>o.ShopId OR r.TenantId<>o.TenantId")>0)problems.Add("存在跨店铺账单关联");
        if(db.Scalar<int>("SELECT COUNT(*) FROM BillRows r JOIN AfterSaleCases c ON c.Id=r.CaseId WHERE r.OrderId<>c.OrderId OR r.Kind<>'Refund'")>0)problems.Add("退款流水售后关联不一致");
        var snapshots=db.Query("SELECT c.ShipmentLineId,c.SkuId,c.QuantityUnits,c.UnitCostMicros,c.CostCents,sc.QuantityUnits,s.State FROM ShipmentCosts c JOIN ShipmentComponents sc ON sc.ShipmentLineId=c.ShipmentLineId AND sc.SkuId=c.SkuId JOIN ShipmentLines l ON l.Id=c.ShipmentLineId JOIN Shipments s ON s.Id=l.ShipmentId",
            r=>(Id:r.GetString(0),Sku:r.GetString(1),Q:r.GetInt64(2),Micro:r.IsDBNull(3)?(long?)null:r.GetInt64(3),Cents:r.IsDBNull(4)?(long?)null:r.GetInt64(4),Original:r.GetInt64(5),State:r.GetString(6)));
        foreach(var c in snapshots)if(c.Q!=c.Original||c.State!="Shipped"||(c.Micro is not null&&ExtendedCost(c.Q,c.Micro.Value)!=c.Cents))problems.Add("出库成本快照不一致："+c.Id+" / "+c.Sku);
        if(db.Scalar<int>("SELECT COUNT(*) FROM ShipmentComponents c JOIN ShipmentLines l ON l.Id=c.ShipmentLineId JOIN Shipments s ON s.Id=l.ShipmentId LEFT JOIN ShipmentCosts cost ON cost.ShipmentLineId=c.ShipmentLineId AND cost.SkuId=c.SkuId WHERE s.State='Shipped' AND cost.ShipmentLineId IS NULL")>0)problems.Add("已出库组件缺少成本占位快照");
        foreach(var e in ExportsRead(db))if(FinanceTextHash(db.Scalar<string>("SELECT JsonContent FROM FinanceExports WHERE Id=$id",("id",e.Id)))!=e.Hash)problems.Add("财务导出哈希不一致："+e.Number);
        foreach(var e in db.Query("SELECT Number,CsvContent,CsvHash FROM FinanceExports",r=>(Number:r.GetString(0),Csv:r.GetString(1),Hash:r.GetString(2))))if(FinanceTextHash(e.Csv)!=e.Hash)problems.Add("财务CSV哈希不一致："+e.Number);
        return new(problems.Count==0,problems);
    }
}
