using CommerceErp.Core;
using static CommerceErp.Infrastructure.CommerceDatabase;

namespace CommerceErp.Infrastructure;

public sealed partial class OrderService
{
    private static long CostMicros(decimal value)
    {
        if (value < 0 || value > 1000000 || decimal.Round(value,6)!=value) throw new BusinessRuleException("单位成本须为 0–1000000 元，最多六位小数；零成本也必须有依据。");
        return checked((long)(value*1000000));
    }
    private static long FeeCents(decimal value)
    {
        if (value < 0 || value > 1000000000 || decimal.Round(value,2)!=value) throw new BusinessRuleException("费用须为 0–1000000000 元，最多两位小数。");
        return checked((long)(value*100));
    }
    private static long ExtendedCost(long quantityUnits,long micros) => checked((long)decimal.Round((decimal)quantityUnits*micros/10000000m,0,MidpointRounding.AwayFromZero));
    private static void CaptureShipmentCosts(DbSession db,string shipment)
    {
        var components=db.Query("SELECT c.ShipmentLineId,c.SkuId,c.QuantityUnits,s.UnitCostMicros,s.Version FROM ShipmentComponents c JOIN ShipmentLines l ON l.Id=c.ShipmentLineId LEFT JOIN SkuCosts s ON s.SkuId=c.SkuId WHERE l.ShipmentId=$id",
            r=>(Line:r.GetString(0),Sku:r.GetString(1),Qty:r.GetInt64(2),Micro:r.IsDBNull(3)?(long?)null:r.GetInt64(3),Version:r.IsDBNull(4)?0:r.GetInt64(4)),("id",shipment));
        foreach(var c in components)
            db.Execute("INSERT INTO ShipmentCosts VALUES($line,$sku,$q,$micro,$cost,1,$source)",("line",c.Line),("sku",c.Sku),("q",c.Qty),("micro",c.Micro),("cost",c.Micro is null?null:(object)ExtendedCost(c.Qty,c.Micro.Value)),("source",c.Micro is null?"出库时未配置成本，待补录":$"出库成本快照：SKU 成本 v{c.Version}"));
    }
    public List<SkuCost> SkuCosts() => _database.Read(db=>db.Query("SELECT s.Id,s.Code,c.UnitCostMicros,COALESCE(c.Version,0),COALESCE(c.Evidence,'') FROM Skus s LEFT JOIN SkuCosts c ON c.SkuId=s.Id WHERE s.Enabled=1 AND s.ComponentsJson='[]' ORDER BY s.Code",
        r=>new SkuCost(r.GetString(0),r.GetString(1),r.IsDBNull(2)?null:r.GetInt64(2)/1000000m,r.GetInt64(3),r.GetString(4))));
    public FinanceResult SetSkuCost(string sku,SetSkuCostRequest r)=>FinanceCommand(r.OperationId,"SetSkuCost",new{sku,r},r.Actor,r.Reason,db=>
    {
        var micros=CostMicros(r.UnitCost); AfterText(r.Evidence,"成本依据",1000);
        if(db.Scalar<int>("SELECT COUNT(*) FROM Skus WHERE Id=$id AND TenantId=$t AND Enabled=1 AND ComponentsJson='[]'",("id",sku),("t",Tenant(db)))!=1) throw new BusinessRuleException("请选择启用的实物 SKU。");
        var version=db.Scalar<long>("SELECT Version FROM SkuCosts WHERE SkuId=$s",("s",sku)); if(version!=r.ExpectedVersion)throw new ConflictException("成本版本已变化，请刷新。");
        db.Upsert("UPDATE SkuCosts SET UnitCostMicros=$c,Version=Version+1,Evidence=$e WHERE SkuId=$s", "INSERT INTO SkuCosts VALUES($s,$c,1,$e)",("s",sku),("c",micros),("e",r.Evidence));
        FinanceHistoryAdd(db,sku,"SetSkuCost",r.Actor,r.Reason,new{r.UnitCost,r.Evidence,version=version+1}); return new(sku,version+1,false,"单位成本已保存，仅影响之后出库；历史快照不变。");
    });
    public FinanceResult FillShipmentCost(string line,string sku,FillCostRequest r)=>FinanceCommand(r.OperationId,"FillShipmentCost",new{line,sku,r},r.Actor,r.Reason,db=>
    {
        var micros=CostMicros(r.UnitCost); AfterText(r.Evidence,"历史成本依据",1000);
        var snapshot=db.Query("SELECT QuantityUnits,UnitCostMicros,Version FROM ShipmentCosts WHERE ShipmentLineId=$l AND SkuId=$s",x=>(Qty:x.GetInt64(0),Missing:x.IsDBNull(1),Version:x.GetInt64(2)),("l",line),("s",sku)).SingleOrDefault();
        if(snapshot.Version==0)throw new BusinessRuleException("出库成本快照不存在。");
        if(!snapshot.Missing || snapshot.Version!=r.ExpectedVersion)throw new ConflictException("只能补录缺失成本一次；已有快照不可覆盖。");
        db.Execute("UPDATE ShipmentCosts SET UnitCostMicros=$m,CostCents=$c,Version=Version+1,Source=$source WHERE ShipmentLineId=$l AND SkuId=$s",("m",micros),("c",ExtendedCost(snapshot.Qty,micros)),("source","历史补录："+r.Evidence),("l",line),("s",sku));
        FinanceHistoryAdd(db,line,"FillCost",r.Actor,r.Reason,new{sku,r.UnitCost,r.Evidence}); return new(line,snapshot.Version+1,false,"历史成本已补录并冻结，库存数量未变化。");
    });
    private static List<CostSnapshot> CostsRead(DbSession db,string? order=null) => db.Query("""
        SELECT c.ShipmentLineId,s.Number,l.OrderId,o.ExternalOrderId,c.SkuId,k.Code,c.QuantityUnits,c.UnitCostMicros,c.CostCents,c.Version,c.Source,
        COALESCE((SELECT SUM(rc.GoodUnits) FROM ReturnComponents rc JOIN AfterSaleLines al ON al.Id=rc.CaseLineId WHERE al.ShipmentLineId=c.ShipmentLineId AND rc.SkuId=c.SkuId),0)
        FROM ShipmentCosts c JOIN ShipmentLines l ON l.Id=c.ShipmentLineId JOIN Shipments s ON s.Id=l.ShipmentId JOIN SalesOrders o ON o.Id=l.OrderId JOIN Skus k ON k.Id=c.SkuId AND k.TenantId=o.TenantId
        WHERE ($o IS NULL OR l.OrderId=$o)
        """+SecurityContext.ShopFilter("o")+" ORDER BY s.CreatedAt DESC,l.Id,k.Code",r=>
        {
            var q=r.GetInt64(6); var good=r.GetInt64(11); decimal? cost=r.IsDBNull(8)?null:r.GetInt64(8)/100m;
            return new CostSnapshot(r.GetString(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetString(4),r.GetString(5),q/1000m,r.IsDBNull(7)?null:r.GetInt64(7)/1000000m,cost,good/1000m,cost is null?null:decimal.Round(cost.Value*good/q,2,MidpointRounding.AwayFromZero),r.GetInt64(9),r.GetString(10));
        },("o",order));
    public List<CostSnapshot> ShipmentCosts(string? order=null)=>_database.Read(db=>CostsRead(db,order));
    private static FeePlan FeePlanRead(DbSession db,string order)=>db.Query("SELECT PlatformFeeCents,FreightCents,Version,Evidence FROM OrderFeePlans WHERE OrderId=$o",r=>new FeePlan(order,r.GetInt64(0)/100m,r.GetInt64(1)/100m,r.GetInt64(2),r.GetString(3)),("o",order)).SingleOrDefault()??new(order,null,null,0,"");
    public FinanceResult SetFeePlan(string id,SetFeePlanRequest r)=>FinanceCommand(r.OperationId,"SetFeePlan",new{id,r},r.Actor,r.Reason,db=>
    {
        _=Get(db,id); var fee=FeeCents(r.PlatformFee); var freight=FeeCents(r.Freight); AfterText(r.Evidence,"费用核对依据",1000);
        var old=FeePlanRead(db,id); if(old.Version!=r.ExpectedVersion)throw new ConflictException("费用计划已变化，请刷新。");
        db.Upsert("UPDATE OrderFeePlans SET PlatformFeeCents=$f,FreightCents=$h,Version=Version+1,Evidence=$e WHERE OrderId=$o", "INSERT INTO OrderFeePlans VALUES($o,$f,$h,1,$e)",("o",id),("f",fee),("h",freight),("e",r.Evidence));
        FinanceHistoryAdd(db,id,"FeePlan",r.Actor,r.Reason,new{old,r.PlatformFee,r.Freight,r.Evidence}); return new(id,old.Version+1,false,"应扣平台费和运费已登记；金额为零同样有明确依据。");
    });
}
