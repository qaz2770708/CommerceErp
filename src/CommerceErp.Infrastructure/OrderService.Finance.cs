using System.Text.Json;
using CommerceErp.Core;
using static CommerceErp.Infrastructure.CommerceDatabase;

namespace CommerceErp.Infrastructure;

public sealed partial class OrderService
{
    private FinanceResult FinanceCommand(string op, string kind, object body, string actor, string reason, Func<DbSession, FinanceResult> run)
    {
        OperationId(op); AfterText(actor, "操作人", 100); Reason(reason);
        string hash; try { hash = Hash(body); } catch (JsonException) { throw new BusinessRuleException("请求中存在无效枚举。"); }
        return _database.Write(db =>
        {
            var prior = Replay<FinanceResult>(db, op, kind, hash); if (prior is not null) return prior with { Replayed = true };
            var result = run(db); SaveOperation(db, op, kind, hash, result); _fault?.Invoke("finance-before-commit"); return result;
        });
    }
    private static void FinanceHistoryAdd(DbSession db, string id, string action, string actor, string reason, object data) =>
        db.Execute("INSERT INTO FinanceHistory(EntityId,Action,Actor,Reason,DataJson,CreatedAt) VALUES($id,$action,$actor,$reason,$data,$now)",
            ("id", id), ("action", action), ("actor", actor), ("reason", reason), ("data", Json(data)), ("now", Now()));
    private static List<FinanceHistory> FinanceHistoryRead(DbSession db, string id) => db.Query("SELECT Id,EntityId,Action,Actor,Reason,DataJson,CreatedAt FROM FinanceHistory WHERE EntityId=$id ORDER BY Id DESC" + db.Limit(200),
        r => new FinanceHistory(r.GetInt64(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetString(4),r.GetString(5),r.GetString(6)), ("id",id));
    private static void CheckBillScope(DbSession db, BillInput input)
    {
        if (input is null || !Enum.IsDefined(input.Platform)) throw new BusinessRuleException("平台无效。");
        BillCsv.Identifier(input.BatchNumber); BillCsv.Identifier(input.FileName);
        if (db.Scalar<int>("SELECT COUNT(*) FROM Shops WHERE TenantId=$t AND Platform=$p AND Id=$s", ("t", Tenant(db)), ("p", input.Platform.ToString()), ("s", input.ShopId)) != 1) throw new BusinessRuleException("请选择已登记的同平台店铺。");
    }
    private static string RowHash(BillCsv.Parsed r) => Hash(new { r.Transaction,r.Order,r.Kind,r.Cents,r.Time,r.Case });
    private static (string? Order, string? Case) AutoBillMatch(DbSession db, Platform platform, string shop, string external, string sourceCase, BillKind kind)
    {
        var order = db.Scalar<string>("SELECT Id FROM SalesOrders WHERE TenantId=$t AND Platform=$p AND ShopId=$shop AND ExternalOrderId=$external", ("t",Tenant(db)),("p",platform.ToString()),("shop",shop),("external",external));
        var caseId = order is null || kind != BillKind.Refund ? null : db.Scalar<string>("SELECT Id FROM AfterSaleCases WHERE OrderId=$o AND SourceCaseNumber=$c AND State IN('Approved','Closed')",("o",order),("c",sourceCase));
        return (order,caseId);
    }
    private static BillPreview PreviewBillCore(DbSession db, BillInput input)
    {
        try
        {
            CheckBillScope(db,input); var parsed = BillCsv.Parse(input); var seen = new Dictionary<string,string>(StringComparer.Ordinal); int added = 0, duplicate = 0; var lines = new List<BillPreviewLine>();
            foreach (var row in parsed)
            {
                var hash = RowHash(row); string? prior = seen.GetValueOrDefault(row.Transaction);
                if (prior is null)
                {
                    var stored = db.Query("SELECT r.SourceHash,b.State FROM BillRows r JOIN BillBatches b ON b.Id=r.BatchId WHERE r.TenantId=$t AND r.Platform=$p AND r.ShopId=$s AND r.TransactionId=$tx", r => (Hash:r.GetString(0),State:r.GetString(1)),("t",Tenant(db)),("p",input.Platform.ToString()),("s",input.ShopId),("tx",row.Transaction)).SingleOrDefault();
                    if (stored.State == "Voided") throw new ConflictException($"流水 {row.Transaction} 属于已作废批次，须核实后使用新的更正流水编号。");
                    prior = stored.Hash;
                }
                if (prior is not null && prior != hash) throw new ConflictException($"流水 {row.Transaction} 已有不同内容，整批不导入。");
                if (prior is null) added++; else duplicate++;
                seen[row.Transaction] = hash; var match = AutoBillMatch(db,input.Platform,input.ShopId,row.Order,row.Case,row.Kind);
                lines.Add(new(row.Line,row.Transaction,row.Order,row.Kind,row.Cents/100m,row.Time,row.Case,prior is not null ? "重复，将跳过" : match.Order is null ? "原订单未匹配" : row.Kind == BillKind.Refund && match.Case is null ? "售后单待匹配" : "业务编号已匹配（非平台核验）"));
            }
            return new(true,FinanceTextHash(input.Content),added,duplicate,[],lines);
        }
        catch (BusinessRuleException e) { return new(false,"",0,0,[e.Message],[]); }
        catch (ConflictException e) { return new(false,"",0,0,[e.Message],[]); }
    }
    public BillPreview PreviewBill(BillInput input) => _database.Read(db => PreviewBillCore(db,input));
    public FinanceResult ImportBill(ImportBillRequest r) => FinanceCommand(r.OperationId,"ImportBill",r,r.Actor,r.Reason,db =>
    {
        CheckBillScope(db,r.Input); var hash = Hash(r.Input);
        var old = db.Query("SELECT Id,RequestHash,Version FROM BillBatches WHERE TenantId=$t AND Platform=$p AND ShopId=$s AND Number=$n", x => (Id:x.GetString(0),Hash:x.GetString(1),Version:x.GetInt64(2)),("t",Tenant(db)),("p",r.Input.Platform.ToString()),("s",r.Input.ShopId),("n",r.Input.BatchNumber)).SingleOrDefault();
        if (old.Id is not null) { if (old.Hash != hash) throw new ConflictException("批次号已用于不同文件或映射。"); return new(old.Id,old.Version,true,"原批次已处理，请刷新核对状态。"); }
        var preview = PreviewBillCore(db,r.Input); if (!preview.Valid) throw new BusinessRuleException(string.Join("；",preview.Errors));
        var id = Guid.NewGuid().ToString("N");
        db.Execute("INSERT INTO BillBatches VALUES($id,$t,$p,$shop,$n,$f,$contentHash,$hash,$content,$mapping,$count,$duplicates,'Active',1,$now)",("id",id),("t",Tenant(db)),("p",r.Input.Platform.ToString()),("shop",r.Input.ShopId),("n",r.Input.BatchNumber),("f",r.Input.FileName),("contentHash",preview.ContentHash),("hash",hash),("content",r.Input.Content),("mapping",Json(new {r.Input.Columns,r.Input.Types})),("count",preview.NewRows),("duplicates",preview.DuplicateRows),("now",Now()));
        foreach (var line in BillCsv.Parse(r.Input))
        {
            if (db.Scalar<int>("SELECT COUNT(*) FROM BillRows WHERE TenantId=$t AND Platform=$p AND ShopId=$s AND TransactionId=$tx",("t",Tenant(db)),("p",r.Input.Platform.ToString()),("s",r.Input.ShopId),("tx",line.Transaction)) > 0) continue;
            var match = AutoBillMatch(db,r.Input.Platform,r.Input.ShopId,line.Order,line.Case,line.Kind);
            db.Execute("INSERT INTO BillRows VALUES($id,$batch,$t,$p,$s,$tx,$ext,$kind,$amount,$time,$case,$hash,$raw,$order,$caseId,1)",("id",Guid.NewGuid().ToString("N")),("batch",id),("t",Tenant(db)),("p",r.Input.Platform.ToString()),("s",r.Input.ShopId),("tx",line.Transaction),("ext",line.Order),("kind",line.Kind.ToString()),("amount",line.Cents),("time",line.Time.ToString("O")),("case",line.Case),("hash",RowHash(line)),("raw",line.Raw),("order",match.Order),("caseId",match.Case));
            _fault?.Invoke("finance-after-row");
        }
        FinanceHistoryAdd(db,id,"ImportBill",r.Actor,r.Reason,new { preview.NewRows,preview.DuplicateRows,preview.ContentHash });
        return new(id,1,false,$"导入 {preview.NewRows} 笔，跳过 {preview.DuplicateRows} 笔重复流水；账单来源未经平台自动核验。");
    });
    public FinanceResult VoidBill(string id, FinanceAction r) => FinanceCommand(r.OperationId,"VoidBill",new{id,r},r.Actor,r.Reason,db =>
    {
        var b = BillBatchRead(db,id); if (b.Version != r.ExpectedVersion || b.State != "Active") throw new ConflictException("批次已变化，请刷新。");
        if (db.Scalar<int>("SELECT COUNT(*) FROM FinanceExportRows e JOIN BillRows r ON r.Id=e.RowId WHERE r.BatchId=$id",("id",id)) > 0) throw new ConflictException("该批次包含已生成财务导出的流水，不能作废；需另行处理冲销。");
        db.Execute("UPDATE BillBatches SET State='Voided',Version=Version+1 WHERE Id=$id",("id",id));
        FinanceHistoryAdd(db,id,"VoidBill",r.Actor,r.Reason,new{}); return new(id,b.Version+1,false,"批次已作废，原记录保留，已从汇总排除。");
    });
    public FinanceResult MatchBill(string id, MatchBillRequest r) => FinanceCommand(r.OperationId,"MatchBill",new{id,r},r.Actor,r.Reason,db =>
    {
        var row = BillRowRead(db,id); if (row.Version != r.ExpectedVersion || row.State != "Active") throw new ConflictException("流水已变化或作废。");
        if (db.Scalar<int>("SELECT COUNT(*) FROM FinanceExportRows WHERE RowId=$id",("id",id)) > 0) throw new ConflictException("已纳入财务导出，不能更改业务关联。");
        AfterText(r.Evidence,"匹配依据",1000); var o = Get(db,r.OrderId);
        if (o.Source.Platform != row.Platform || o.Source.ShopId != row.ShopId) throw new BusinessRuleException("不能跨平台或店铺匹配。");
        if (row.Kind == BillKind.Refund)
        {
            if (string.IsNullOrEmpty(r.CaseId) || db.Scalar<int>("SELECT COUNT(*) FROM AfterSaleCases WHERE Id=$c AND OrderId=$o AND State IN('Approved','Closed')",("c",r.CaseId),("o",r.OrderId)) != 1) throw new BusinessRuleException("退款须关联同一订单的已批准售后单。");
        }
        else if (!string.IsNullOrEmpty(r.CaseId)) throw new BusinessRuleException("非退款行不能关联售后。");
        db.Execute("UPDATE BillRows SET OrderId=$o,CaseId=$c,Version=Version+1 WHERE Id=$id",("o",r.OrderId),("c",string.IsNullOrEmpty(r.CaseId)?null:r.CaseId),("id",id));
        FinanceHistoryAdd(db,id,"ManualMatch",r.Actor,r.Reason,new { oldOrder=row.OrderId,oldCase=row.CaseId,r.OrderId,r.CaseId,r.Evidence });
        return new(id,row.Version+1,false,"人工关联已保存；原始流水未改变，也未核验平台资金。");
    });
    public FinanceResult RematchBills(FinanceAction r) => FinanceCommand(r.OperationId,"RematchBills",r,r.Actor,r.Reason,db =>
    {
        int count=0;
        foreach (var row in BillRowsRead(db).Where(x=>x.State=="Active" && (x.OrderId is null || x.Kind==BillKind.Refund && x.CaseId is null)))
        {
            var m=AutoBillMatch(db,row.Platform,row.ShopId,row.ExternalOrderId,row.SourceCase,row.Kind);
            // A manual order association must never silently be overwritten by a later automatic pass.
            if (row.OrderId is not null && row.OrderId!=m.Order) continue;
            if (m.Order is null || (m.Order==row.OrderId && m.Case==row.CaseId)) continue;
            db.Execute("UPDATE BillRows SET OrderId=$o,CaseId=$c,Version=Version+1 WHERE Id=$id",("o",m.Order),("c",m.Case),("id",row.Id));
            FinanceHistoryAdd(db,row.Id,"Rematch",r.Actor,r.Reason,new {m.Order,m.Case}); count++;
        }
        return new("rematch",1,false,$"重新匹配 {count} 笔；已有人工关联未覆盖。");
    });
}
