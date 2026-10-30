using System.Reflection;
using System.Text.Json;
using CommerceErp.Core;
using CommerceErp.Infrastructure;
using Microsoft.SqlServer.TransactSql.ScriptDom;

var root=Path.GetFullPath(args[1]);var output=Path.GetFullPath(args[2]);Directory.CreateDirectory(Path.GetDirectoryName(output)!);
var results=new List<object>();var failed=0;
void Assert(bool condition){if(!condition)throw new InvalidOperationException("断言失败。");}
async Task Test(string name,Func<Task> action){try{await action();results.Add(new{name,passed=true});Console.WriteLine("PASS "+name);}catch(Exception e){failed++;results.Add(new{name,passed=false,error=e.GetType().Name});Console.WriteLine("FAIL "+name+" "+e.GetType().Name);}}
Task Sync(Action action){action();return Task.CompletedTask;}
string Op()=>Guid.NewGuid().ToString("N");
await Test("SQL Server 2019 建表脚本通过微软 T-SQL 解析器",()=>Sync(()=>
{
    using var reader=new StringReader(File.ReadAllText(Path.Combine(root,"src/CommerceErp.Infrastructure/Migrations/SqlServer/001_schema.sql")));
    new TSql150Parser(true).Parse(reader,out var errors);
    foreach(var e in errors)Console.WriteLine($"SQL line {e.Line}: {e.Message}");Assert(errors.Count==0);
}));
await Test("SQL Server 账号增量升级脚本通过微软 T-SQL 解析器",()=>Sync(()=>
{
    using var reader=new StringReader(File.ReadAllText(Path.Combine(root,"src/CommerceErp.Infrastructure/Migrations/SqlServer/006_accounts.sql")));
    new TSql150Parser(true).Parse(reader,out var errors);Assert(errors.Count==0);
}));
await Test("SQL Server任务增量升级脚本通过微软T-SQL解析器",()=>Sync(()=>
{using var reader=new StringReader(File.ReadAllText(Path.Combine(root,"src/CommerceErp.Infrastructure/Migrations/SqlServer/007_tasks.sql")));new TSql150Parser(true).Parse(reader,out var errors);Assert(errors.Count==0);}));
await Test("SQL Server主数据升级脚本通过微软T-SQL解析器",()=>Sync(()=>
{using var reader=new StringReader(File.ReadAllText(Path.Combine(root,"src/CommerceErp.Infrastructure/Migrations/SqlServer/008_master.sql")));new TSql150Parser(true).Parse(reader,out var errors);foreach(var e in errors)Console.WriteLine(e.Message);Assert(errors.Count==0);}));
await Test("审批增量升级脚本通过TSQL解析",()=>Sync(()=>{using var reader=new StringReader(File.ReadAllText(Path.Combine(root,"src/CommerceErp.Infrastructure/Migrations/SqlServer/009_review.sql")));new TSql150Parser(true).Parse(reader,out var errors);Assert(errors.Count==0);}));
await Test("业务保存点SQL解析为事务语句而非过程调用",()=>Sync(()=>{foreach(var action in new[]{"save","rollback"}){var sql=DbSession.SavepointSql(true,action,"reserve_all");var tree=(TSqlScript)new TSql150Parser(true).Parse(new StringReader(sql),out var errors);Assert(errors.Count==0&&tree.Batches.Single().Statements.Single() is not ExecuteStatement);}Assert(DbSession.SavepointSql(true,"release","reserve_all")==""&&DbSession.SavepointSql(false,"rollback","reserve_all")=="ROLLBACK TO reserve_all");var rejected=false;try{DbSession.SavepointSql(true,"save","bad; DROP TABLE x");}catch(ArgumentException){rejected=true;}Assert(rejected);}));
await Test("库存单据升级脚本通过TSQL解析",()=>Sync(()=>{using var reader=new StringReader(File.ReadAllText(Path.Combine(root,"src/CommerceErp.Infrastructure/Migrations/SqlServer/010_inventory.sql")));new TSql150Parser(true).Parse(reader,out var errors);Assert(errors.Count==0);}));
if(args[0]=="--sql")
{
    var connection=Environment.GetEnvironmentVariable("ERP_SQL_TEST_CONNECTION")??throw new ArgumentException("缺少 ERP_SQL_TEST_CONNECTION。仅允许指定已创建的专用空测试库。");
    var databaseName=args[3];var snapshotDir=Path.Combine(Path.GetDirectoryName(output)!,"snapshot-"+Op());
    var snapshot=CommerceDatabase.CreateVerifiedSnapshot(Path.Combine(root,"tests/Fixtures/stage5-v4.db"),snapshotDir);
    var migrated=CommerceDatabase.MigrateToSqlServer(snapshot,connection,databaseName);
    await File.WriteAllTextAsync(output+".migration.json",CommerceDatabase.Json(migrated));
    var database=CommerceDatabase.SqlServer(connection);var s=new OrderService(database);
    await Test("SQL主数据草稿修改作废审核与分页专项",()=>Sync(()=>{var m=new MasterDataService(database);var sku=m.SaveSku(new(Op(),null,0,"SQL-TRIAL-"+Op(),"SQL试运行商品","件",true,[],"专项测试"));var stock=s.Inventory().Single(x=>x.SkuId==sku.Id);var line=new OpeningLine(sku.Id,3,1,stock.Version);var doc=m.CreateOpening(new(Op(),Op(),[line],"专项草稿"));var edit=new EditOpeningRequest(Op(),1,[line with{Quantity=5}],"修改数量");m.EditOpening(doc.Id,edit);Assert(m.EditOpening(doc.Id,edit).Replayed);m.VoidOpening(doc.Id,new(Op(),2,"作废"));Assert(s.Inventory().Single(x=>x.SkuId==sku.Id).OnHand==0);var next=m.CreateOpening(new(Op(),Op(),[line],"新建正确单"));m.ApproveOpening(next.Id,new(Op(),1,"审核"));Assert(s.Inventory().Single(x=>x.SkuId==sku.Id).OnHand==3&&m.SkuPage(search:"SQL-TRIAL").Total==1&&m.OpeningPage().Total>=2);_=m.MappingPage();_=m.UnmappedPage();}));
    await Test("SQL库存调整盘点隔离与冲销专项",()=>Sync(()=>{var docs=new InventoryDocumentService(database);var b=docs.GetBalance("sku-shirt");InventoryDocumentLine Line(decimal value,decimal q=0){var current=docs.GetBalance("sku-shirt");return new("sku-shirt",value,q,current.Version,current.QuarantineVersion,current.SkuVersion);}var draft=docs.Create(new(Op(),Op(),InventoryDocumentKind.Count,[Line(b.OnHand,b.Quarantined+3)],"实物核对","SQL合成盘点"));docs.Approve(draft.Id,new(Op(),1,"审核"));var release=docs.Create(new(Op(),Op(),InventoryDocumentKind.QuarantineRelease,[Line(2)],"复检","凭证"));docs.Approve(release.Id,new(Op(),1,"审核"));var reverse=docs.Reverse(release.Id,new(Op(),2,Op(),"更正","实物退回"));docs.Approve(reverse.Id,new(Op(),1,"审核"));Assert(docs.Audit().Passed&&s.CheckConsistency().Passed&&s.AuditAfterSales().Passed&&docs.GetBalance("sku-shirt").Quarantined==b.Quarantined+3);}));
    await Test("全部业务表迁移后行数与内容摘要相符",()=>Sync(()=>Assert(migrated.Status=="Committed"&&migrated.Tables.Count>=40)));
    await Test("共享库身份与源库区分",()=>Sync(()=>Assert(database.InstanceId!=new CommerceDatabase(snapshotDir).InstanceId)));
    await Test("迁移保留财务导出和退款历史",()=>Sync(()=>Assert(s.FinanceExports().Count>0&&s.AfterSales().Total>0)));
    await Test("共享库各分页、搜索和历史查询可用",()=>Sync(()=>
    {
        _=s.List(1,20,"a");_=s.Shipments(1,20,"a");_=s.AfterSales(1,20,"a");_=s.ShippingCandidates();_=s.AfterSaleCandidates();_=s.Deliveries();_=s.StockLedger();_=s.QuarantineLedger();
        foreach(var o in s.List().Items){_=s.Details(o.Id);_=s.FinanceOrderDetails(o.Id);}
        foreach(var c in s.AfterSales().Items)_=s.AfterSaleDetails(c.Id);
    }));
    await Test("多个数据库连接重复操作只生效一次",async()=>
    {
        var before=s.Inventory().Single(x=>x.SkuId=="sku-shirt").OnHand;var request=new ReceiveStockRequest(Op(),1,"SQL 幂等联调");
        await Task.WhenAll(Enumerable.Range(0,10).Select(_=>Task.Run(()=>new OrderService(CommerceDatabase.SqlServer(connection)).Receive("sku-shirt",request))));
        Assert(s.Inventory().Single(x=>x.SkuId=="sku-shirt").OnHand==before+1);
    });
    await Test("事务中途异常回滚",()=>Sync(()=>
    {
        var before=s.Inventory().Single(x=>x.SkuId=="sku-shirt").OnHand;
        try{database.Write<bool>(db=>{db.Execute("UPDATE InventoryBalances SET OnHandUnits=OnHandUnits+1000 WHERE SkuId='sku-shirt'");throw new IOException("故障注入");});}catch(IOException){}
        Assert(s.Inventory().Single(x=>x.SkuId=="sku-shirt").OnHand==before);
    }));
    await Test("共享映射和准备记录版本控制",async()=>
    {
        using var store=new WorkspaceStore(database);var data=await store.ReadAsync();var map=data.Catalog.Mappings.First();
        await store.SaveMappingAsync(new(map,map.Version));var readiness=data.Readiness.First();await store.SaveReadinessAsync(readiness.Platform,new(readiness.Revision,readiness.Items));
        await store.ValidateAsync(SeedData.BaseOrder(Platform.Douyin));Assert((await store.ReportsAsync()).Count>0);
    });
    await Test("订单到出库、退款、对账的共享数据库闭环",()=>Sync(()=>
    {
        var cost=s.SkuCosts().Single(x=>x.SkuId=="sku-shirt");s.SetSkuCost("sku-shirt",new(Op(),cost.Version,3,"SQL 联调成本","测试员","设置"));
        var baseOrder=SeedData.BaseOrder(Platform.Douyin);
        var order=baseOrder with {ExternalOrderId=Op(),ShippingFee=0,OrderDiscount=0,PaidAmount=10,Lines=[baseOrder.Lines[0]with{Quantity=2,GoodsAmount=10,MerchantDiscount=0}]};
        var o=s.Import(new(Op(),order));Assert(o.State==ReviewState.Approved);
        s.SaveShippingProfile(o.OrderId,new(Op(),o.Version,new("测试用户","00000000000","省","市","区","联调地址"),true,"测试员","保存"));
        var preparation=s.PrepareShipping(o.OrderId);
        var shipment=s.CreateShipment(new(Op(),preparation.Lines.Select(l=>new AllocationLine(o.OrderId,l.ExternalLineId,l.Remaining,preparation.Order.Version)).ToList(),"测试员","配单")).Id;
        WarehouseActionRequest Wh()=>new(Op(),s.ShipmentDetails(shipment).Summary.Version,"测试员","联调");
        s.StartPicking(shipment,Wh());foreach(var check in s.ShipmentDetails(shipment).Checks)s.ScanShipment(shipment,new(Op(),s.ShipmentDetails(shipment).Summary.Version,check.SkuCode,check.Required,"测试员"));
        s.PackShipment(shipment,Wh());s.SaveWaybill(shipment,new(Op(),s.ShipmentDetails(shipment).Summary.Version,"TEST",Op(),"测试员","运单"));s.DispatchShipment(shipment,Wh());
        var after=s.PrepareAfterSale(o.OrderId);var sourceCase=Op();
        var c=s.CreateAfterSale(new(Op(),o.OrderId,after.Order.Version,sourceCase,AfterSaleKind.ReturnRefund,5,after.Lines.Select(l=>new AfterSaleSelection(l.ShipmentLineId,1)).ToList(),"测试员","申请")).Id;
        s.ApproveAfterSale(c,new(Op(),1,"测试员","同意"));var detail=s.AfterSaleDetails(c);var component=detail.Components.First();
        s.InspectReturn(c,new(Op(),detail.Summary.Version,Op(),"TEST",[new(component.CaseLineId,component.SkuId,1,0)],"测试员","收货"));
        s.RecordRefund(c,new(Op(),s.AfterSaleDetails(c).Summary.Version,Op(),RefundTrackingState.EvidenceRecorded,5,Op(),"模拟凭证",DateTimeOffset.UtcNow,true,"测试员","退款"));
        s.CloseAfterSale(c,new(Op(),s.AfterSaleDetails(c).Summary.Version,"测试员","结案"));
        var fee=s.FinanceOrderDetails(o.OrderId).FeePlan;s.SetFeePlan(o.OrderId,new(Op(),fee.Version,0,0,"无费用","测试员","核对"));
        var csv=$"流水号,订单号,类型,金额,发生时间,币种,售后编号\r\n{Op()},{order.ExternalOrderId},收入,10,2026-10-01T12:00:00+08:00,CNY,\r\n{Op()},{order.ExternalOrderId},退款,5,2026-10-01T12:00:00+08:00,CNY,{sourceCase}\r\n";
        s.ImportBill(new(Op(),new(Platform.Douyin,SeedData.Shop(Platform.Douyin),Op(),"SQL联调.csv",csv,new(),new(){{"收入",BillKind.Sale},{"退款",BillKind.Refund}}),"测试员","账单"));
        Assert(s.FinanceOrderDetails(o.OrderId).Order.Profit==2);
        var export=s.CreateFinanceExport(new(Op(),Op(),"SQL联调",[o.OrderId],"测试员","导出"));Assert(s.FinanceExportDocument(export.Id).Json.Length>0);
    }));
    await Test("迁移和新增操作后四类一致性审计全部通过",()=>Sync(()=>Assert(s.CheckConsistency().Passed&&s.AuditWarehouse().Passed&&s.AuditAfterSales().Passed&&s.AuditFinance().Passed)));
    var accounts=new AccountService(database);const string pass="Sql-Fixture-Initial-2026!",changed="Sql-Fixture-Changed-2026!";
    var admin=accounts.Setup(new("sql-admin","SQL测试管理员",pass));
    var user=accounts.Save(null,new("sql-finance","SQL单店财务",AccountRole.Finance,SeedData.Tenant,true,0,[SeedData.Shop(Platform.Douyin)],[BusinessUnits.Warehouse],"SQL范围验证",pass));
    var initial=accounts.Login(new(user.Username,pass));accounts.ChangePassword(initial.Token,new(pass,changed));var login=accounts.Login(new(user.Username,changed));
    await Test("SQL财务范围过滤及相关查询在同一事务内生效",()=>Sync(()=>
    {
        SecurityContext.Current=new(AccountService.TokenHash(login.Token),login.Account,"finance.read","sql-test","GET","",false,(_,_)=>{});
        try{Assert(s.FinanceOrders().Items.All(x=>x.ShopId==SeedData.Shop(Platform.Douyin)));_=s.FinanceReport();_=s.FinanceExports();_=s.ShipmentCosts();Assert(s.BillRows().Items.All(x=>x.ShopId==SeedData.Shop(Platform.Douyin)));}
        finally{SecurityContext.Current=null;}
    }));
    await Test("SQL审计筛选分页和JSON导出可用",()=>Sync(()=>
    {var audit=accounts.QueryAudit(new(PageSize:1));Assert(audit.Total>0&&audit.Items.Count==1);var exported=accounts.ExportAudit(new(new(SnapshotId:audit.SnapshotId),"SQL导出测试"));Assert(exported.Rows==audit.Total&&exported.Json.Length>0);}));
    await Test("SQL业务事件领取及阻断，分页详情和人工重试",async()=>
    {
        var tasks=new WorkTaskService(database);var processor=new OutboxProcessor(database,[]);await processor.ProcessNextAsync();
        var page=tasks.List(1,100,state:"Blocked",kind:"BusinessEvent");Assert(page.Items.Count>0&&tasks.Summary().Total>=page.Total);
        var row=page.Items.First();var before=s.Inventory().Single(x=>x.SkuId=="sku-shirt");var request=new RetryWorkTaskRequest(Op(),row.Version,"SQL测试员","核查后重新排队");
        var retried=tasks.Retry(row.Kind,row.Id,request);Assert(tasks.Retry(row.Kind,row.Id,request).Replayed&&retried.Id==row.Id);
        Assert(tasks.Details(row.Kind,row.Id).History.Any(x=>x.Action=="ManualRetry")&&s.Inventory().Single(x=>x.SkuId=="sku-shirt")==before);
    });
    await Test("SQL任务列表和总数按财务店铺范围过滤",()=>Sync(()=>
    {
        SecurityContext.Current=new(AccountService.TokenHash(login.Token),login.Account,"tasks.read","sql-test","GET","",false,(_,_)=>{});
        try{var tasks=new WorkTaskService(database);var page=tasks.List(1,100);Assert(page.Items.All(x=>x.ShopId==SeedData.Shop(Platform.Douyin)&&x.Kind==WorkTaskKind.BusinessEvent));Assert(tasks.Summary().Total==page.Total);}
        finally{SecurityContext.Current=null;}
    }));
    await Test("SQL账号改权后原会话在事务内被拒绝",()=>Sync(()=>
    {
        accounts.Save(user.Id,new(user.Username,user.DisplayName,user.Role,user.TenantId,true,login.Account.Version,[SeedData.Shop(Platform.Taobao)],user.Warehouses,"SQL调店"));
        SecurityContext.Current=new(AccountService.TokenHash(login.Token),login.Account,"finance.read","sql-test","GET","",false,(_,_)=>{});
        try{try{s.FinanceOrders();throw new Exception("旧会话仍有效");}catch(CommerceErp.Core.AuthenticationException){}}
        finally{SecurityContext.Current=null;}
    }));
    await Test("SQL本机身份核对与管理员恢复",()=>Sync(()=>
    {var identity=CommerceDatabase.InspectExisting(null,connection);Assert(identity.InstanceId==database.InstanceId&&identity.Schema==10);var recovered=accounts.RecoverAdministrator(admin.Username,changed,"SQL恢复演练",identity.InstanceId);Assert(recovered.MustChangePassword&&accounts.QueryAudit(new(Action:"RecoverAdministrator")).Total==1);}));
    await Test("禁止覆盖非空目标库",()=>Sync(()=>{try{CommerceDatabase.MigrateToSqlServer(snapshot,connection,databaseName);throw new Exception("错误覆盖");}catch(InvalidDataException){}}));
}
await File.WriteAllTextAsync(output,JsonSerializer.Serialize(new{passed=failed==0,total=results.Count,failures=failed,sqlServerIntegration=args[0]=="--sql"?"Executed":"NotRun_NoInstance",checks=results},new JsonSerializerOptions{WriteIndented=true}));
return failed==0?0:1;

