using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CommerceErp.Core;
using CommerceErp.Desktop;
using CommerceErp.Infrastructure;

var output=Path.GetFullPath(args[0]);Directory.CreateDirectory(output);var repo=Path.GetFullPath(args[1]);
string Op()=>Guid.NewGuid().ToString("N");
var data=Path.Combine(output,"data-"+Op());var results=new List<object>();int failed=0;
void Assert(bool value,string message="断言失败"){if(!value)throw new Exception(message);}
async Task Test(string name,Func<Task> action){try{await action();results.Add(new{name,passed=true});Console.WriteLine("PASS "+name);}catch(Exception e){failed++;results.Add(new{name,passed=false,error=e.ToString()});Console.WriteLine("FAIL "+name+": "+e.Message);}}
Task Sync(Action action){action();return Task.CompletedTask;}
void Throws<T>(Action action)where T:Exception{try{action();}catch(T){return;}throw new Exception("预期异常未发生："+typeof(T).Name);}
var gateway=Op()+Op();var config=Path.Combine(output,"server.json");
await File.WriteAllTextAsync(config,JsonSerializer.Serialize(new{listenAddress="127.0.0.1",port=0,databaseProvider="Sqlite",dataDirectory=data}));
var start=new ProcessStartInfo("dotnet"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
start.ArgumentList.Add(Path.Combine(repo,"src/CommerceErp.Api/bin/Release/net10.0/CommerceErp.Api.dll"));start.ArgumentList.Add("--server");start.ArgumentList.Add(config);start.Environment["ERP_SESSION_TOKEN"]=gateway;
using var process=Process.Start(start)!;var errors=process.StandardError.ReadToEndAsync();
try
{
    using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(25));Uri? address=null;
    while(await process.StandardOutput.ReadLineAsync(timeout.Token) is{} line)if(line.StartsWith("ERP_READY|")){address=new(line[10..]);break;}
    if(address is null)throw new Exception("测试服务启动失败");
    using var http=new HttpClient{BaseAddress=address};http.DefaultRequestHeaders.Add("X-Erp-Session",gateway);
    async Task<(int Code,string Text)> Send(string method,string path,object? body=null,string? token=null)
    {using var req=new HttpRequestMessage(new HttpMethod(method),path);if(token is not null)req.Headers.Authorization=new("Bearer",token);if(body is not null)req.Content=JsonContent.Create(body,options:ErpJson.Options);using var res=await http.SendAsync(req);return((int)res.StatusCode,await res.Content.ReadAsStringAsync());}
    async Task<T> Ok<T>(string method,string path,object? body=null,string? token=null)
    {var r=await Send(method,path,body,token);Assert(r.Code==200,$"{method} {path}: {r.Code} {r.Text}");return JsonSerializer.Deserialize<T>(r.Text,ErpJson.Options)!;}
    Task<LoginResult> Login(string name,string pass)=>Ok<LoginResult>("POST","/api/security/login",new LoginRequest(name,pass));
    const string initial="Fixture-Initial-2026!",changed="Fixture-Changed-2026!",recovery="Fixture-Recovery-2026!";
    var admin=await Ok<AccountInfo>("POST","/api/security/setup",new SetupAccountRequest("scope-admin","权限测试管理员",initial));
    var adminToken=(await Login("scope-admin",initial)).Token;
    var db=new CommerceDatabase(data);var s=new OrderService(db);var accounts=new AccountService(db);
    var shop=SeedData.Shop(Platform.Douyin);var otherShop=SeedData.Shop(Platform.Taobao);
    var users=new Dictionary<AccountRole,LoginResult>();
    foreach(var role in new[]{AccountRole.Warehouse,AccountRole.Finance,AccountRole.Observer})
    {
        var name="scoped-"+role.ToString().ToLowerInvariant();
        await Ok<AccountInfo>("POST","/api/security/accounts",new SaveAccountRequest(name,role.ToString(),role,SeedData.Tenant,true,0,[shop],[BusinessUnits.Warehouse],"建立单店铺角色",initial),adminToken);
        var login=await Login(name,initial);await Ok<JsonElement>("POST","/api/security/password",new ChangePasswordRequest(initial,changed),login.Token);users[role]=await Login(name,changed);
    }
    var wh=users[AccountRole.Warehouse];var fin=users[AccountRole.Finance];var observer=users[AccountRole.Observer];
    SampleOrder Source(Platform p,string? external=null){var b=SeedData.BaseOrder(p);return b with{ExternalOrderId=external??Op(),ShippingFee=0,OrderDiscount=0,PaidAmount=10,Lines=[b.Lines[0]with{Quantity=1,GoodsAmount=10,MerchantDiscount=0}]};}
    ShippingAddress customerAddress=new("敏感测试收件人","00012345678","测试省","测试市","敏感测试区","敏感测试街道999号","123456");
    string Order(Platform p,string? external=null)
    {var o=s.Import(new(Op(),Source(p,external)));Assert(o.State==ReviewState.Approved);s.SaveShippingProfile(o.OrderId,new(Op(),o.Version,customerAddress,true,"测试员","合成地址"));return o.OrderId;}
    List<AllocationLine> Lines(params string[] ids)=>ids.SelectMany(id=>{var p=s.PrepareShipping(id);return p.Lines.Select(l=>new AllocationLine(id,l.ExternalLineId,l.Remaining,p.Order.Version));}).ToList();
    string Ship(params string[] ids)
    {var id=s.CreateShipment(new(Op(),Lines(ids),"测试员","合成发货")).Id;WarehouseActionRequest W()=>new(Op(),s.ShipmentDetails(id).Summary.Version,"测试员","合成发货");s.StartPicking(id,W());foreach(var c in s.ShipmentDetails(id).Checks)s.ScanShipment(id,new(Op(),s.ShipmentDetails(id).Summary.Version,c.SkuCode,c.Required,"测试员"));s.PackShipment(id,W());s.SaveWaybill(id,new(Op(),s.ShipmentDetails(id).Summary.Version,"TEST",Op(),"测试员","合成运单"));s.DispatchShipment(id,W());return id;}
    BillInput Input(Platform p,string external)=>new(p,SeedData.Shop(p),Op(),"合成账单.csv",$"流水号,订单号,类型,金额,发生时间,币种,售后编号\r\n{Op()},{external},收入,10,2026-10-01T12:00:00+08:00,CNY,\r\n",new(),new(){{"收入",BillKind.Sale}});
    void Reconcile(string id){var f=s.FinanceOrderDetails(id);s.SetFeePlan(id,new(Op(),f.FeePlan.Version,0,0,"合成依据","测试员","费用核对"));s.ImportBill(new(Op(),Input(f.Order.Platform,f.Order.ExternalOrderId),"测试员","合成导入"));}
    foreach(var cost in s.SkuCosts())s.SetSkuCost(cost.SkuId,new(Op(),cost.Version,2,"合成依据","测试员","初始化"));
    // Same-shop multi-order shipment exercises a ledger row whose OrderId is NULL.
    var d1=Order(Platform.Douyin);var d2=Order(Platform.Douyin);var t1=Order(Platform.Taobao);var t2=Order(Platform.Taobao);
    var dShip=Ship(d1,d2);var tShip=Ship(t1,t2);foreach(var id in new[]{d1,d2,t1,t2})Reconcile(id);
    var d3=Order(Platform.Douyin);var t3=Order(Platform.Taobao);
    var ownExport=s.CreateFinanceExport(new(Op(),Op(),"测试财务",[d1],"测试员","单店导出"));
    var mixedExport=s.CreateFinanceExport(new(Op(),Op(),"测试财务",[d2,t2],"测试员","跨店导出"));
    var otherBatch=s.BillBatches().First(x=>x.ShopId==otherShop);var otherRow=s.BillRows().Items.First(x=>x.ShopId==otherShop);var ownRow=s.BillRows().Items.First(x=>x.ShopId==shop);
    var otherCost=s.ShipmentCosts(t1).First();var otherDelivery=s.Deliveries().First(x=>x.OrderId==t1);
    async Task Denied(string method,string path,object? body,string token){var r=await Send(method,path,body,token);Assert(r.Code==403,$"{path}: expected 403, got {r.Code} {r.Text}");}
    await Test("仓储账号可配置单店铺，候选、分页总数、发货队列均隔离",async()=>
    {Assert(wh.Account.Shops.SequenceEqual([shop]));var candidates=await Ok<List<ShippingCandidate>>("GET","/api/warehouse/orders",token:wh.Token);Assert(candidates.Count==3&&candidates.All(x=>x.ShopId==shop));var page=await Ok<ShipmentPage>("GET","/api/warehouse/shipments?pageSize=1",token:wh.Token);Assert(page.Total==1&&page.Items.Single().Id==dShip);var deliveries=await Ok<List<DeliveryJobView>>("GET","/api/warehouse/deliveries",token:wh.Token);Assert(deliveries.Count==2&&deliveries.All(x=>x.ShipmentId==dShip));});
    await Test("仓储详情地址及回传重试不可跨店访问",async()=>
    {foreach(var path in new[]{"/api/warehouse/orders/"+t3,"/api/warehouse/shipments/"+tShip})await Denied("GET",path,null,wh.Token);await Denied("POST","/api/warehouse/orders/"+t3+"/address",new SaveShippingProfileRequest(Op(),1,customerAddress,true,"伪造","测试"),wh.Token);await Denied("POST","/api/warehouse/deliveries/"+otherDelivery.Id+"/retry",new WarehouseActionRequest(Op(),1,"伪造","测试"),wh.Token);});
    foreach(var action in new[]{"split","pick","scan","reset","pack","waybill","void","dispatch"})
        await Test("跨店出库动作拒绝："+action,()=>Denied("POST","/api/warehouse/shipments/"+tShip+"/"+action,new{operationId=Op(),expectedVersion=1,actor="伪造",reason="跨店测试"},wh.Token));
    await Test("配单和合单检查每个订单与出库单",async()=>
    {await Denied("POST","/api/warehouse/shipments",new CreateShipmentRequest(Op(),Lines(d3,t3),"伪造","混入跨店订单"),wh.Token);await Denied("POST","/api/warehouse/merge",new MergeShipmentsRequest(Op(),[new(dShip,1),new(tShip,1)],"伪造","混入跨店出库单"),wh.Token);});
    await Test("单店仓储可以保存本店地址及配单",async()=>
    {await Ok<WarehouseResult>("POST","/api/warehouse/orders/"+d3+"/address",new SaveShippingProfileRequest(Op(),s.Details(d3).Summary.Version,customerAddress,true,"伪造","本店地址"),wh.Token);var shipment=await Ok<WarehouseResult>("POST","/api/warehouse/shipments",new CreateShipmentRequest(Op(),Lines(d3),"伪造","本店配单"),wh.Token);Assert(s.ShipmentDetails(shipment.Id).Lines.Single().OrderId==d3);});
    await Test("共享仓余额不按店铺伪拆分，含合单的流水按店铺隔离",async()=>
    {var inventory=await Ok<List<InventoryRow>>("GET","/api/inventory",token:wh.Token);Assert(JsonSerializer.Serialize(inventory)==JsonSerializer.Serialize(s.Inventory()));var ledger=await Ok<List<StockLedgerRow>>("GET","/api/inventory/ledger",token:wh.Token);var hidden=db.Read(d=>d.Query("SELECT Id FROM InventoryLedger WHERE ShipmentId=$id OR OrderId IN($a,$b,$c)",r=>r.GetInt64(0),("id",tShip),("a",t1),("b",t2),("c",t3)));Assert(hidden.Count>0&&!ledger.Any(x=>hidden.Contains(x.Id)));var ownShipLedger=db.Read(d=>d.Scalar<long>("SELECT Id FROM InventoryLedger WHERE ShipmentId=$id",("id",dShip)));Assert(ledger.Any(x=>x.Id==ownShipLedger));});
    await Test("共享配置写入及全库核对要求全店授权",async()=>
    {await Denied("POST","/api/warehouse/barcodes",new SaveBarcodeRequest(Op(),0,"TEST123","sku-shirt","测试","全局"),wh.Token);await Denied("POST","/api/finance/sku-costs/sku-shirt",new SetSkuCostRequest(Op(),1,1,"依据","测试","全局"),fin.Token);foreach(var path in new[]{"/api/warehouse/audit","/api/business/consistency"})await Denied("GET",path,null,wh.Token);await Denied("GET","/api/finance/audit",null,fin.Token);});
    await Test("只读角色的地址、出库列表、历史与回传信息在服务端隐藏",async()=>
    {var detail=await Ok<ShipmentDetails>("GET","/api/warehouse/shipments/"+dShip,token:observer.Token);Assert(detail.Address.Recipient=="[已隐藏]"&&detail.Address.Phone=="[已隐藏]"&&detail.Address.Street=="[已隐藏]"&&detail.History.All(x=>x.Data=="[已隐藏]"));Assert(!JsonSerializer.Serialize(detail).Contains(customerAddress.Phone));var page=await Ok<ShipmentPage>("GET","/api/warehouse/shipments",token:observer.Token);Assert(page.Items.All(x=>x.Recipient=="[已隐藏]"));var prep=await Ok<OrderShippingPreparation>("GET","/api/warehouse/orders/"+d3,token:observer.Token);Assert(prep.Profile!.Address.PostalCode=="[已隐藏]");var original=await Ok<ShipmentDetails>("GET","/api/warehouse/shipments/"+dShip,token:wh.Token);Assert(original.Address==customerAddress);});
    await Test("财务分页、账单、成本及汇总均按本店过滤",async()=>
    {var orders=await Ok<FinanceOrderPage>("GET","/api/finance/orders?pageSize=1",token:fin.Token);Assert(orders.Total==3&&orders.Items.All(x=>x.ShopId==shop));var batches=await Ok<List<BillBatch>>("GET","/api/finance/batches",token:fin.Token);Assert(batches.Count==2&&batches.All(x=>x.ShopId==shop));var rows=await Ok<BillPage>("GET","/api/finance/rows?pageSize=1",token:fin.Token);Assert(rows.Total==2&&rows.Items.All(x=>x.ShopId==shop));var costs=await Ok<List<CostSnapshot>>("GET","/api/finance/costs",token:fin.Token);Assert(costs.Count==2&&costs.All(x=>x.OrderId==d1||x.OrderId==d2));var report=await Ok<FinanceReport>("GET","/api/finance/report",token:fin.Token);Assert(report.Orders==3&&report.Shops.Count==1);});
    await Test("账单原文、行详情、订单成本不能通过ID越权读取",async()=>
    {foreach(var path in new[]{"/api/finance/batches/"+otherBatch.Id+"/original","/api/finance/rows/"+otherRow.Id,"/api/finance/orders/"+t1,"/api/finance/costs?order="+t1})await Denied("GET",path,null,fin.Token);});
    await Test("跨店账单预览导入作废及行匹配被拒绝",async()=>
    {var input=Input(Platform.Taobao,Op());await Denied("POST","/api/finance/preview",input,fin.Token);await Denied("POST","/api/finance/batches",new ImportBillRequest(Op(),input,"测试","跨店"),fin.Token);await Denied("POST","/api/finance/batches/"+otherBatch.Id+"/void",new FinanceAction(Op(),1,"测试","跨店"),fin.Token);await Denied("POST","/api/finance/rows/"+otherRow.Id+"/match",new MatchBillRequest(Op(),1,d1,null,"依据","测试","跨店行"),fin.Token);await Denied("POST","/api/finance/rows/"+ownRow.Id+"/match",new MatchBillRequest(Op(),1,t1,null,"依据","测试","跨店目标"),fin.Token);});
    await Test("跨店费用和历史出库成本写入被拒绝",async()=>
    {await Denied("POST","/api/finance/orders/"+t1+"/fees",new SetFeePlanRequest(Op(),1,0,0,"依据","测试","跨店"),fin.Token);await Denied("POST","/api/finance/costs/"+otherCost.ShipmentLineId+"/"+otherCost.SkuId,new FillCostRequest(Op(),1,1,"依据","测试","跨店"),fin.Token);});
    await Test("导出包只在包含的全部订单均获授权时可见和下载",async()=>
    {var exports=await Ok<List<FinanceExport>>("GET","/api/finance/exports",token:fin.Token);Assert(exports.Count==1&&exports[0].Id==ownExport.Id);Assert((await Ok<FinanceExportDocument>("GET","/api/finance/exports/"+ownExport.Id,token:fin.Token)).Summary.Orders==1);await Denied("GET","/api/finance/exports/"+mixedExport.Id,null,fin.Token);await Denied("POST","/api/finance/exports/"+mixedExport.Id+"/saved",new FinanceAction(Op(),1,"测试","跨店"),fin.Token);await Denied("POST","/api/finance/exports",new CreateFinanceExportRequest(Op(),Op(),"测试",[d1,t1],"测试","跨店"),fin.Token);});
    await Test("本店财务可创建导出包并幂等重试",async()=>
    {var freshOrder=Order(Platform.Douyin);Ship(freshOrder);Reconcile(freshOrder);var req=new CreateFinanceExportRequest(Op(),Op(),"测试",[freshOrder],"伪造","授权导出");var r=await Ok<FinanceResult>("POST","/api/finance/exports",req,fin.Token);var retry=await Ok<FinanceResult>("POST","/api/finance/exports",req,fin.Token);Assert(retry.Replayed&&retry.Id==r.Id);});
    await Test("自动重新匹配只修改本店未匹配流水",async()=>
    {var externalD=Op();var externalT=Op();var own=await Ok<FinanceResult>("POST","/api/finance/batches",new ImportBillRequest(Op(),Input(Platform.Douyin,externalD),"测试","先到账单"),fin.Token);var other=s.ImportBill(new(Op(),Input(Platform.Taobao,externalT),"测试","先到账单"));var orderD=Order(Platform.Douyin,externalD);Order(Platform.Taobao,externalT);await Ok<FinanceResult>("POST","/api/finance/rematch",new FinanceAction(Op(),1,"测试","重新匹配"),fin.Token);var rows=s.BillRows().Items;Assert(rows.Single(x=>x.BatchId==own.Id).OrderId==orderD&&rows.Single(x=>x.BatchId==other.Id).OrderId is null);});
    foreach(var role in new[]{AccountRole.Warehouse,AccountRole.Finance,AccountRole.Observer})
        await Test(role+" 单店铺桌面模型可完整初始化",async()=>
        {await using var client=new LocalApiClient();await client.StartRemoteAsync(address.AbsoluteUri,gateway,Path.Combine(output,"client-"+role));await client.LoginAsync(users[role].Account.Username,changed);var vm=new MainViewModel(client);await vm.InitializeAsync();Assert(!client.HasAllShops&&!vm.AccountsCommand.CanExecute(null));Assert(!vm.Finance.AuditCommand.CanExecute(null)&&!vm.Warehouse.AuditCommand.CanExecute(null));});
    var taskService=new WorkTaskService(db);
    var taskD=db.Read(x=>x.Scalar<string>("SELECT Id FROM Outbox WHERE AggregateId=$id AND EventType='OrderAccepted'",("id",d1)));
    var taskT=db.Read(x=>x.Scalar<string>("SELECT Id FROM Outbox WHERE AggregateId=$id AND EventType='OrderAccepted'",("id",t1)));
    for(var i=0;i<100&&taskService.Details(WorkTaskKind.BusinessEvent,taskD).Task.State!="Blocked";i++)await Task.Delay(50);
    var csUser=await Ok<AccountInfo>("POST","/api/security/accounts",new SaveAccountRequest("task-customer","任务客服",AccountRole.CustomerService,SeedData.Tenant,true,0,[shop],[BusinessUnits.Warehouse],"任务权限测试",initial),adminToken);
    var csInitial=await Login(csUser.Username,initial);await Ok<JsonElement>("POST","/api/security/password",new ChangePasswordRequest(initial,changed),csInitial.Token);var cs=await Login(csUser.Username,changed);
    await Test("任务接口要求账号登录",async()=>Assert((await Send("GET","/api/tasks")).Code==401));
    await Test("任务列表与汇总按店铺及模块权限过滤",async()=>
    {foreach(var user in users.Values.Append(cs)){var list=await Ok<WorkTaskPage>("GET","/api/tasks?pageSize=100",token:user.Token);var summary=await Ok<WorkTaskSummary>("GET","/api/tasks/summary",token:user.Token);Assert(list.Total==summary.Total&&list.Items.All(x=>x.ShopId==shop||x.ShopId==""));if(user.Account.Role is AccountRole.Finance or AccountRole.CustomerService)Assert(list.Items.All(x=>x.Kind==WorkTaskKind.BusinessEvent&&!x.RelatedId.StartsWith("stock:")));}});
    await Test("跨店任务详情和重试不能通过直接接口访问",async()=>
    {foreach(var user in new[]{wh,fin,observer,cs})await Denied("GET","/api/tasks/BusinessEvent/"+taskT,null,user.Token);await Denied("POST","/api/tasks/BusinessEvent/"+taskT+"/retry",new RetryWorkTaskRequest(Op(),1,"伪造","跨店"),cs.Token);await Denied("GET","/api/tasks/ShipmentDelivery/"+otherDelivery.Id,null,wh.Token);});
    await Test("角色不能通过任务重试绕过原业务权限",async()=>
    {foreach(var user in new[]{wh,fin,observer})await Denied("POST","/api/tasks/BusinessEvent/"+taskD+"/retry",new RetryWorkTaskRequest(Op(),1,"伪造","测试"),user.Token);});
    await Test("授权任务重试幂等、留痕且不重做订单或库存",async()=>
    {var before=JsonSerializer.Serialize(s.Inventory());var row=taskService.Details(WorkTaskKind.BusinessEvent,taskD).Task;var req=new RetryWorkTaskRequest(Op(),row.Version,"伪造操作人","核查任务重试");await Ok<WorkTaskResult>("POST","/api/tasks/BusinessEvent/"+taskD+"/retry",req,cs.Token);Assert((await Ok<WorkTaskResult>("POST","/api/tasks/BusinessEvent/"+taskD+"/retry",req,cs.Token)).Replayed);Assert(before==JsonSerializer.Serialize(s.Inventory()));var history=taskService.Details(WorkTaskKind.BusinessEvent,taskD).History.Where(x=>x.Action=="ManualRetry").ToList();Assert(history.Count==1&&history[0].Actor==AccountService.Actor(cs.Account));await Denied("POST","/api/tasks/BusinessEvent/"+taskD+"/retry",req,adminToken);});
    await Test("仓储能重试共享库存事件但客服不可读取",async()=>
    {s.Receive("sku-shirt",new(Op(),1,"任务重试夹具"));var id=db.Read(x=>x.Scalar<string>("SELECT Id FROM Outbox WHERE AggregateId='stock:sku-shirt'"));for(var i=0;i<100&&taskService.Details(WorkTaskKind.BusinessEvent,id).Task.State!="Blocked";i++)await Task.Delay(50);await Denied("GET","/api/tasks/BusinessEvent/"+id,null,cs.Token);var row=taskService.Details(WorkTaskKind.BusinessEvent,id).Task;var before=s.Inventory().Single(x=>x.SkuId=="sku-shirt").OnHand;await Ok<WorkTaskResult>("POST","/api/tasks/BusinessEvent/"+id+"/retry",new RetryWorkTaskRequest(Op(),row.Version,"伪造","核查库存事件"),wh.Token);Assert(before==s.Inventory().Single(x=>x.SkuId=="sku-shirt").OnHand);});
    await Test("任务中心发货重试沿用原发货幂等与操作历史",async()=>
    {var job=s.Deliveries().First(x=>x.OrderId==d1);var before=JsonSerializer.Serialize(s.Inventory());var req=new RetryWorkTaskRequest(Op(),job.Version,"伪造","从任务中心重新回查");await Ok<WorkTaskResult>("POST","/api/tasks/ShipmentDelivery/"+job.Id+"/retry",req,wh.Token);Assert((await Ok<WorkTaskResult>("POST","/api/tasks/ShipmentDelivery/"+job.Id+"/retry",req,wh.Token)).Replayed);var detail=await Ok<WorkTaskDetails>("GET","/api/tasks/ShipmentDelivery/"+job.Id,token:wh.Token);Assert(detail.History.Any(h=>h.Action=="ManualRetry"&&h.Actor==AccountService.Actor(wh.Account))&&before==JsonSerializer.Serialize(s.Inventory()));});
    await Test("任务详情不返回原始载荷回执或收件信息",async()=>
    {var raw=await Send("GET","/api/tasks/BusinessEvent/"+taskD,token:observer.Token);Assert(raw.Code==200&&!raw.Text.Contains("payloadJson")&&!raw.Text.Contains("receipt")&&!raw.Text.Contains(customerAddress.Phone));});
    await Test("任务筛选和错误类型输入有确定响应",async()=>
    {Assert((await Send("GET","/api/tasks?state=NotAState",token:adminToken)).Code==400);Assert((await Send("GET","/api/tasks?page=0",token:adminToken)).Code==400);Assert((await Send("GET","/api/tasks/99/"+taskD,token:adminToken)).Code==400);var rows=await Ok<WorkTaskPage>("GET","/api/tasks?kind=BusinessEvent&search="+taskD,token:cs.Token);Assert(rows.Total==1&&rows.Items.Single().Id==taskD);});
    await Test("任务账号改权后旧会话不能重试",async()=>
    {var u=cs.Account;await Ok<AccountInfo>("PUT","/api/security/accounts/"+u.Id,new SaveAccountRequest(u.Username,u.DisplayName,u.Role,u.TenantId,true,u.Version,[otherShop],u.Warehouses,"客服调店"),adminToken);Assert((await Send("POST","/api/tasks/BusinessEvent/"+taskD+"/retry",new RetryWorkTaskRequest(Op(),1,"伪造","旧会话"),cs.Token)).Code==401);});
    await Test("审计查询和导出仅管理员可用",async()=>
    {foreach(var user in users.Values){await Denied("GET","/api/security/audit/query",null,user.Token);await Denied("POST","/api/security/audit/export",new AuditExportRequest(new(),"导出"),user.Token);}});
    var first=await Ok<AuditPage>("GET","/api/security/audit/query?pageSize=2&outcome=Denied",token:adminToken);
    await Test("审计分页使用固定快照，新增事件不挤动旧页",async()=>
    {Assert(first.Total>2&&first.Items.Count==2);await Denied("GET","/api/security/accounts",null,fin.Token);var again=await Ok<AuditPage>("GET",$"/api/security/audit/query?pageSize=2&outcome=Denied&snapshotId={first.SnapshotId}",token:adminToken);var second=await Ok<AuditPage>("GET",$"/api/security/audit/query?page=2&pageSize=2&outcome=Denied&snapshotId={first.SnapshotId}",token:adminToken);Assert(first.Total==again.Total&&first.Items.Select(x=>x.Id).SequenceEqual(again.Items.Select(x=>x.Id))&&!first.Items.Select(x=>x.Id).Intersect(second.Items.Select(x=>x.Id)).Any());});
    await Test("审计可组合账号动作资源时间筛选",async()=>
    {var from=Uri.EscapeDataString(DateTimeOffset.UtcNow.AddHours(-1).ToString("O"));var to=Uri.EscapeDataString(DateTimeOffset.UtcNow.AddHours(1).ToString("O"));var page=await Ok<AuditPage>("GET",$"/api/security/audit/query?actor={fin.Account.Username}&action=AccessDenied&outcome=Denied&resource=security&from={from}&to={to}",token:adminToken);Assert(page.Total>0&&page.Items.All(x=>x.ActorName==fin.Account.Username&&x.Action=="AccessDenied"&&x.Outcome=="Denied"&&x.Resource.Contains("security")));});
    await Test("非法审计参数返回输入错误",async()=>
    {foreach(var q in new[]{"page=0","pageSize=201","snapshotId=-1","outcome=Unknown","from=2026-10-01"})Assert((await Send("GET","/api/security/audit/query?"+q,token:adminToken)).Code==400,q);});
    await Test("审计导出遵循筛选快照、附带校验摘要并记录原因",async()=>
    {var doc=await Ok<AuditExportDocument>("POST","/api/security/audit/export",new AuditExportRequest(new(Actor:fin.Account.Username,Outcome:"Denied",SnapshotId:first.SnapshotId),"测试核查导出"),adminToken);var hash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(doc.Json))).ToLowerInvariant();Assert(hash==doc.Sha256&&doc.Rows>0&&doc.SnapshotId==first.SnapshotId);using var json=JsonDocument.Parse(doc.Json);var rows=json.RootElement.GetProperty("rows");Assert(rows.GetArrayLength()==doc.Rows&&rows.EnumerateArray().All(r=>r.GetProperty("actorName").GetString()==fin.Account.Username));Assert(accounts.QueryAudit(new(Action:"ExportAudit")).Items.Any(x=>x.Reason.Contains(doc.Sha256)));await File.WriteAllTextAsync(Path.Combine(output,"audit-export.json"),doc.Json);});
    await Test("审计导出超过一万条拒绝且不提交成功记录",()=>Sync(()=>
    {db.Write(d=>{for(int i=0;i<10001;i++)d.Execute("INSERT INTO SecurityAudit(ActorId,ActorName,Action,Resource,Outcome,OperationId,CreatedAt,Reason) VALUES('bulk','bulk','BulkTest','','Committed','',$now,'合成边界数据')",("now",DateTimeOffset.UtcNow.ToString("O")));return true;});var before=accounts.QueryAudit(new(Action:"ExportAudit")).Total;Throws<BusinessRuleException>(()=>accounts.ExportAudit(new(new(Action:"BulkTest"),"超限测试")));Assert(accounts.QueryAudit(new(Action:"ExportAudit")).Total==before);}));
    await Test("仓储质检与财务退款不可操作跨店售后",async()=>
    {var otherCase=s.CreateAfterSale(new(Op(),t3,s.Details(t3).Summary.Version,Op(),AfterSaleKind.CancelUnshipped,10,[],"测试","跨店售后夹具"));await Denied("GET","/api/after-sales/cases/"+otherCase.Id,null,wh.Token);await Denied("POST","/api/after-sales/cases/"+otherCase.Id+"/inspect",new{operationId=Op(),expectedVersion=1,actor="测试",reason="跨店质检"},wh.Token);await Denied("POST","/api/after-sales/cases/"+otherCase.Id+"/refund",new{operationId=Op(),expectedVersion=1,actor="测试",reason="跨店退款"},fin.Token);});
    await Test("财务范围变更撤销旧会话，新会话不能下载旧店导出包",async()=>
    {var u=fin.Account;await Ok<AccountInfo>("PUT","/api/security/accounts/"+u.Id,new SaveAccountRequest(u.Username,u.DisplayName,u.Role,u.TenantId,true,u.Version,[otherShop],u.Warehouses,"财务调店"),adminToken);Assert((await Send("GET","/api/finance/exports/"+ownExport.Id,token:fin.Token)).Code==401);var changedLogin=await Login(u.Username,changed);await Denied("GET","/api/finance/exports/"+ownExport.Id,null,changedLogin.Token);Assert(!(await Ok<List<FinanceExport>>("GET","/api/finance/exports",token:changedLogin.Token)).Any(x=>x.Id==ownExport.Id));});
    await Test("本机身份检查不创建数据库也不改写已有文件",()=>Sync(()=>
    {var identityDirectory=Path.Combine(output,"identity-only");var isolated=new CommerceDatabase(identityDirectory);var file=Path.Combine(identityDirectory,"commerce.db");var before=SHA256.HashData(File.ReadAllBytes(file));var identity=CommerceDatabase.InspectExisting(identityDirectory,null);Assert(identity.InstanceId==isolated.InstanceId&&identity.Schema==10&&before.SequenceEqual(SHA256.HashData(File.ReadAllBytes(file))));var absent=Path.Combine(output,"absent");Throws<FileNotFoundException>(()=>CommerceDatabase.InspectExisting(absent,null));Assert(!Directory.Exists(absent));}));
    await Test("管理员恢复拒绝错误库身份及普通账号，原会话不受影响",async()=>
    {Throws<ConflictException>(()=>accounts.RecoverAdministrator(admin.Username,recovery,"错误库",Op()));Throws<BusinessRuleException>(()=>accounts.RecoverAdministrator(fin.Account.Username,recovery,"禁止提权",db.InstanceId));Assert((await Send("GET","/api/security/me",token:adminToken)).Code==200);});
    await Test("本机恢复管理员会撤销全部会话并强制改密",async()=>
    {db.Write(d=>d.Execute("UPDATE SecurityUsers SET Enabled=0,FailedAttempts=5,LockedUntil=$until WHERE Id=$id",("until",DateTimeOffset.UtcNow.AddHours(1).ToString("O")),("id",admin.Id)));var recovered=accounts.RecoverAdministrator(admin.Username,recovery,"模拟管理员失联恢复",db.InstanceId);Assert(recovered.Enabled&&recovered.MustChangePassword&&recovered.Shops.Count==4);foreach(var token in users.Values.Select(x=>x.Token).Append(adminToken))Assert((await Send("GET","/api/security/me",token:token)).Code==401);var login=await Login(admin.Username,recovery);Assert(login.Account.MustChangePassword&&(await Send("GET","/api/orders",token:login.Token)).Code==403);await Ok<JsonElement>("POST","/api/security/password",new ChangePasswordRequest(recovery,changed),login.Token);var active=await Login(admin.Username,changed);Assert((await Send("GET","/api/orders",token:active.Token)).Code==200);Assert(accounts.QueryAudit(new(Action:"RecoverAdministrator")).Items.Single().ActorName==Environment.UserDomainName+"\\"+Environment.UserName);});
    await Test("管理员恢复没有网络入口",async()=>Assert((await Send("POST","/api/security/recover",new{username=admin.Username})).Code==404));
}
finally{if(!process.HasExited){process.Kill(true);await process.WaitForExitAsync();}await errors;}
await File.WriteAllTextAsync(Path.Combine(output,"scope-results.json"),JsonSerializer.Serialize(new{passed=failed==0,total=results.Count,failed,results},ErpJson.Options));
return failed==0?0:1;
