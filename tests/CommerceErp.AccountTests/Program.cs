using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using CommerceErp.Core;
using CommerceErp.Desktop;
using CommerceErp.Infrastructure;
using CommerceErp.Api;

var output=Path.GetFullPath(args[0]);Directory.CreateDirectory(output);var repo=Path.GetFullPath(args[1]);
var data=Path.Combine(output,"data-"+Guid.NewGuid().ToString("N"));
var results=new List<object>();int failed=0;
void Assert(bool value,string message="断言失败"){if(!value)throw new Exception(message);}
async Task Test(string name,Func<Task> action){try{await action();results.Add(new{name,passed=true});Console.WriteLine("PASS "+name);}catch(Exception e){failed++;results.Add(new{name,passed=false,error=e.Message});Console.WriteLine("FAIL "+name+": "+e.Message);}}
async Task Reject<T>(Func<Task> action) where T:Exception{try{await action();}catch(T){return;}throw new Exception("预期异常未发生");}
var gateway=Guid.NewGuid().ToString("N")+Guid.NewGuid().ToString("N");
var config=Path.Combine(output,"server.json");await File.WriteAllTextAsync(config,JsonSerializer.Serialize(new{listenAddress="127.0.0.1",port=0,databaseProvider="Sqlite",dataDirectory=data}));
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
    {
        using var request=new HttpRequestMessage(new HttpMethod(method),path);
        if(token is not null)request.Headers.Authorization=new("Bearer",token);
        if(body is not null)request.Content=JsonContent.Create(body,options:ErpJson.Options);
        using var response=await http.SendAsync(request);return((int)response.StatusCode,await response.Content.ReadAsStringAsync());
    }
    async Task<T> Success<T>(string method,string path,object? body=null,string? token=null)
    {var r=await Send(method,path,body,token);Assert(r.Code==200,$"{method} {path}: {r.Code} {r.Text}");return JsonSerializer.Deserialize<T>(r.Text,ErpJson.Options)!;}
    Task<LoginResult> Login(string name,string password)=>Success<LoginResult>("POST","/api/security/login",new LoginRequest(name,password));
    const string initial="Fixture-Initial-2026!", changed="Fixture-Changed-2026!";
    var admin=await Success<AccountInfo>("POST","/api/security/setup",new SetupAccountRequest("admin","测试管理员",initial));
    var adminLogin=await Login("admin",initial);var adminToken=adminLogin.Token;
    var db=new CommerceDatabase(data);var accountService=new AccountService(db);
    var all=SeedData.Platforms.Select(p=>SeedData.Shop(p.Id)).ToList();var douyin=SeedData.Shop(Platform.Douyin);
    SaveAccountRequest Request(AccountInfo u)=>new(u.Username,u.DisplayName,u.Role,u.TenantId,u.Enabled,u.Version,u.Shops,u.Warehouses,"权限测试修改");
    await Test("连接令牌不能代替账号登录",async()=>Assert((await Send("GET","/api/orders")).Code==401));
    await Test("首次管理员入口仅能成功一次",async()=>Assert((await Send("POST","/api/security/setup",new SetupAccountRequest("other","其他",initial))).Code==409));
    await Test("密码和会话令牌只保存哈希",()=>{Assert(db.Read(d=>d.Scalar<string>("SELECT PasswordHash FROM SecurityUsers WHERE Id=$id",("id",admin.Id)))!=initial);Assert(db.Read(d=>d.Scalar<string>("SELECT TokenHash FROM SecuritySessions"))==AccountService.TokenHash(adminToken));return Task.CompletedTask;});
    await Test("最后一个管理员不能停用或降级",async()=>{Assert((await Send("PUT","/api/security/accounts/"+admin.Id,Request(admin) with{Enabled=false},adminToken)).Code==409);Assert((await Send("PUT","/api/security/accounts/"+admin.Id,Request(admin) with{Role=AccountRole.Observer},adminToken)).Code==409);});
    var users=new Dictionary<AccountRole,LoginResult>();
    foreach(var role in new[]{AccountRole.CustomerService,AccountRole.Warehouse,AccountRole.Finance,AccountRole.Observer})
    {
        var name="role-"+role.ToString().ToLowerInvariant();
        var user=await Success<AccountInfo>("POST","/api/security/accounts",new SaveAccountRequest(name,role.ToString(),role,SeedData.Tenant,true,0,role==AccountRole.CustomerService?[douyin]:all,[BusinessUnits.Warehouse],"建立测试角色",initial),adminToken);
        var login=await Login(name,initial);
        await Test(role+" 临时密码只能改密不能进入业务",async()=>{Assert(login.Account.MustChangePassword);Assert((await Send("GET","/api/orders",token:login.Token)).Code==403);Assert((await Send("GET","/api/security/me",token:login.Token)).Code==200);});
        await Success<JsonElement>("POST","/api/security/password",new ChangePasswordRequest(initial,changed),login.Token);
        await Test(role+" 修改密码立即撤销旧会话",async()=>Assert((await Send("GET","/api/security/me",token:login.Token)).Code==401));
        users[role]=await Login(name,changed);
    }
    var customer=users[AccountRole.CustomerService];var warehouse=users[AccountRole.Warehouse];var finance=users[AccountRole.Finance];var observer=users[AccountRole.Observer];
    foreach(var entry in new[]{(customer,"/api/finance/orders",403),(customer,"/api/inventory",403),(warehouse,"/api/inventory",200),(warehouse,"/api/finance/orders",403),(finance,"/api/finance/orders",200),(finance,"/api/warehouse/shipments",403),(observer,"/api/finance/orders",200),(observer,"/api/inventory",200)})
        await Test(entry.Item1.Account.Role+" 读取权限 "+entry.Item2,async()=>Assert((await Send("GET",entry.Item2,token:entry.Item1.Token)).Code==entry.Item3));
    await Test("非管理员不能管理账号或查看访问审计",async()=>{foreach(var login in users.Values){Assert((await Send("GET","/api/security/accounts",token:login.Token)).Code==403);Assert((await Send("GET","/api/security/audit",token:login.Token)).Code==403);}});
    var receipt=new ReceiveStockRequest(Guid.NewGuid().ToString("N"),1,"权限测试补货");
    await Test("只读角色不能通过直接 HTTP 补货",async()=>Assert((await Send("POST","/api/inventory/sku-shirt/receive",receipt,observer.Token)).Code==403));
    await Test("仓储角色可补货并以原操作号幂等重试",async()=>{var before=(await Success<List<InventoryRow>>("GET","/api/inventory",token:warehouse.Token)).Single(x=>x.SkuId=="sku-shirt").OnHand;await Success<JsonElement>("POST","/api/inventory/sku-shirt/receive",receipt,warehouse.Token);await Success<JsonElement>("POST","/api/inventory/sku-shirt/receive",receipt,warehouse.Token);Assert((await Success<List<InventoryRow>>("GET","/api/inventory",token:warehouse.Token)).Single(x=>x.SkuId=="sku-shirt").OnHand==before+1);});
    await Test("其他账号不能接管已归属的操作号",async()=>Assert((await Send("POST","/api/inventory/sku-shirt/receive",receipt,adminToken)).Code==403));
    await Test("操作标识大小写不能绕过账号归属",async()=>{var alternate=new Dictionary<string,object>{{"OperationId",receipt.OperationId},{"Quantity",receipt.Quantity},{"Reason",receipt.Reason}};Assert((await Send("POST","/api/inventory/sku-shirt/receive",alternate,adminToken)).Code==403);});
    await Test("重复及错误类型字段返回输入错误",async()=>{Assert((await Send("POST","/api/inventory/sku-shirt/receive",new{operationId=123,quantity=1,reason="类型错误"},adminToken)).Code==400);var duplicate=new Dictionary<string,object>{{"operationId",receipt.OperationId},{"OperationId",Guid.NewGuid().ToString("N")},{"quantity",1},{"reason","重复字段"}};Assert((await Send("POST","/api/inventory/sku-shirt/receive",duplicate,adminToken)).Code==400);});
    var ownOrder=SeedData.Samples().Single(s=>s.Descriptor.Id=="douyin-valid").Order;
    var otherOrder=SeedData.Samples().Single(s=>s.Descriptor.Id=="taobao-bundle").Order;
    var own=await Success<OrderCommandResult>("POST","/api/orders",new ImportOrderRequest(Guid.NewGuid().ToString("N"),ownOrder),customer.Token);
    var other=await Success<OrderCommandResult>("POST","/api/orders",new ImportOrderRequest(Guid.NewGuid().ToString("N"),otherOrder),adminToken);
    await Test("订单列表分页与总数按授权店铺过滤",async()=>{var page=await Success<OrderPage>("GET","/api/orders?pageSize=1",token:customer.Token);Assert(page.Total==1&&page.Items.Single().ShopId==douyin);Assert((await Success<BusinessDashboard>("GET","/api/business/dashboard",token:customer.Token)).TotalOrders==1);});
    await Test("跨店铺详情导入修改均拒绝",async()=>{Assert((await Send("GET","/api/orders/"+other.OrderId,token:customer.Token)).Code==403);Assert((await Send("POST","/api/orders",new ImportOrderRequest(Guid.NewGuid().ToString("N"),otherOrder),customer.Token)).Code==403);Assert((await Send("POST","/api/orders/"+other.OrderId+"/review",new{operationId=Guid.NewGuid().ToString("N"),expectedVersion=1,reason="跨店"},customer.Token)).Code==403);});
    await Test("初始数据和样例按店铺过滤",async()=>{var b=await Success<BootstrapData>("GET","/api/bootstrap",token:customer.Token);Assert(b.Platforms.Count==1&&b.Catalog.Mappings.All(m=>m.ShopId==douyin));Assert((await Send("GET","/api/samples/taobao-bundle",token:customer.Token)).Code==403);});
    await Test("售后订单候选与详情按店铺过滤",async()=>{var rows=await Success<List<AfterSaleCandidate>>("GET","/api/after-sales/orders",token:customer.Token);Assert(rows.Count==1&&rows[0].OrderId==own.OrderId);Assert((await Send("GET","/api/after-sales/orders/"+other.OrderId,token:customer.Token)).Code==403);});
    var ownSummary=(await Success<OrderDetails>("GET","/api/orders/"+own.OrderId,token:customer.Token)).Summary;
    var caseRequest=new{operationId=Guid.NewGuid().ToString("N"),orderId=own.OrderId,expectedOrderVersion=ownSummary.Version,sourceCaseNumber="SEC-CANCEL-1",kind="CancelOrder",refundAmount=ownOrder.PaidAmount,lines=Array.Empty<object>(),actor="FORGED OPERATOR",reason="授权取消申请"};
    // Use the domain's explicit enum spelling instead of a separate JSON convention.
    var cancellation=Enum.GetValues<AfterSaleKind>().First(k=>k.ToString().Contains("Cancel"));
    var caseBody=JsonSerializer.SerializeToNode(caseRequest,ErpJson.Options)!.AsObject();caseBody["kind"]=cancellation.ToString();
    var caseResult=await Success<AfterSaleResult>("POST","/api/after-sales/cases",caseBody,customer.Token);
    await Test("客户端伪造操作人被登录身份替换",()=>{var actors=db.Read(d=>d.Query("SELECT Actor FROM AfterSaleHistory WHERE CaseId=$id",r=>r.GetString(0),("id",caseResult.Id)));Assert(actors.Count>0&&actors.All(a=>a==AccountService.Actor(customer.Account)));return Task.CompletedTask;});
    var otherSummary=(await Success<OrderDetails>("GET","/api/orders/"+other.OrderId,token:adminToken)).Summary;
    var otherCase=await Success<AfterSaleResult>("POST","/api/after-sales/cases",new CreateAfterSaleRequest(Guid.NewGuid().ToString("N"),other.OrderId,otherSummary.Version,"SEC-CANCEL-2",AfterSaleKind.CancelUnshipped,otherOrder.PaidAmount,[],"伪造","另一店铺申请"),adminToken);
    await Test("售后列表与单据详情隔离店铺",async()=>{var page=await Success<AfterSalePage>("GET","/api/after-sales/cases",token:customer.Token);Assert(page.Total==1&&page.Items.Single().Id==caseResult.Id);Assert((await Send("GET","/api/after-sales/cases/"+otherCase.Id,token:customer.Token)).Code==403);});
    await Test("跨店铺创建售后被拒绝",async()=>{caseBody["orderId"]=other.OrderId;caseBody["operationId"]=Guid.NewGuid().ToString("N");Assert((await Send("POST","/api/after-sales/cases",caseBody,customer.Token)).Code==403);});
    await Test("客户端账号绑定阻止跨人重试",async()=>{
        var profile=Path.Combine(output,"client");await using(var client=new LocalApiClient()){await client.StartRemoteAsync(address.AbsoluteUri,gateway,profile);await client.LoginAsync(customer.Account.Username,changed);await File.WriteAllTextAsync(Path.Combine(profile,"pending-commands.json"),JsonSerializer.Serialize(new Dictionary<string,LocalApiClient.PendingCommand>{["uncertain"]=new("/api/orders","{}")},ErpJson.Options));}
        await using var next=new LocalApiClient();await next.StartRemoteAsync(address.AbsoluteUri,gateway,profile);await Reject<InvalidDataException>(()=>next.LoginAsync("admin",initial));Assert(next.PendingCount==1&&next.CurrentAccount is null);await next.LoginAsync(customer.Account.Username,changed);Assert(next.CurrentAccount?.Id==customer.Account.Id);
    });
    await Test("限制店铺的客服可完整初始化桌面视图模型",async()=>{await using var client=new LocalApiClient();await client.StartRemoteAsync(address.AbsoluteUri,gateway,Path.Combine(output,"limited-ui"));await client.LoginAsync(customer.Account.Username,changed);var vm=new MainViewModel(client);await vm.InitializeAsync();Assert(vm.Operations.Orders.Count==1&&!vm.FinancePageCommand.CanExecute(null)&&!vm.AccountsCommand.CanExecute(null));});
    await Test("账号更新使用版本控制并保留修改原因",async()=>{var updated=await Success<AccountInfo>("PUT","/api/security/accounts/"+customer.Account.Id,Request(customer.Account) with{DisplayName="客服修改"},adminToken);Assert(updated.Version==customer.Account.Version+1);Assert((await Send("PUT","/api/security/accounts/"+updated.Id,Request(customer.Account),adminToken)).Code==409);Assert(accountService.AuditLog().Any(a=>a.Action=="SaveAccount"&&a.Resource==updated.Id&&a.Reason=="权限测试修改"));});
    await Test("权限修改后的旧会话立即失效",async()=>Assert((await Send("GET","/api/orders",token:customer.Token)).Code==401));
    await Test("数据库事务内再次检查会话撤销",async()=>{SecurityContext.Current=new(AccountService.TokenHash(customer.Token),customer.Account,"orders.write","test","POST","",false,(_,_)=>{});try{await Reject<CommerceErp.Core.AuthenticationException>(()=>{db.Write(d=>d.Execute("UPDATE InventoryBalances SET OnHandUnits=OnHandUnits+1000"));return Task.CompletedTask;});}finally{SecurityContext.Current=null;}});
    await Test("停用账号撤销会话并拒绝再次登录",async()=>{await Success<AccountInfo>("PUT","/api/security/accounts/"+observer.Account.Id,Request(observer.Account) with{Enabled=false},adminToken);Assert((await Send("GET","/api/orders",token:observer.Token)).Code==401);Assert((await Send("POST","/api/security/login",new LoginRequest(observer.Account.Username,changed))).Code==401);});
    await Test("管理员重置密码撤销会话且要求再次改密",async()=>{await Success<JsonElement>("POST","/api/security/accounts/"+finance.Account.Id+"/password",new ResetPasswordRequest(finance.Account.Version,initial,"交接重置"),adminToken);Assert((await Send("GET","/api/orders",token:finance.Token)).Code==401);var login=await Login(finance.Account.Username,initial);Assert(login.Account.MustChangePassword);});
    await Test("会话过期后不能访问业务",async()=>{db.Write(d=>d.Execute("UPDATE SecuritySessions SET ExpiresAt=$past WHERE TokenHash=$hash",("past",DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O")),("hash",AccountService.TokenHash(warehouse.Token))));Assert((await Send("GET","/api/inventory",token:warehouse.Token)).Code==401);});
    await Test("连续五次错误密码锁定账号",async()=>{for(int i=0;i<5;i++)Assert((await Send("POST","/api/security/login",new LoginRequest(customer.Account.Username,"incorrect password"))).Code==401);Assert((await Send("POST","/api/security/login",new LoginRequest(customer.Account.Username,changed))).Code==401);});
    await Test("店铺及仓库范围必须来自现有主数据",async()=>{var request=Request(admin) with{Username="invalid",ExpectedVersion=0,InitialPassword=initial,Shops=["unknown-shop"]};Assert((await Send("POST","/api/security/accounts",request,adminToken)).Code==400);});
    await Test("未声明策略的接口默认拒绝",()=>{try{EndpointPermissions.Resolve("POST","/api/new-feature");throw new Exception("未拒绝");}catch(AccessDeniedException){}return Task.CompletedTask;});
    await Test("业务异常回滚时不提交账号审计或操作归属",async()=>{var op=Guid.NewGuid().ToString("N");SecurityContext.Current=new(AccountService.TokenHash(adminToken),admin,"inventory.write","/test/rollback","POST",op,true,(_,_)=>{});try{await Reject<InvalidOperationException>(()=>{db.Write<int>(d=>{d.Execute("UPDATE InventoryBalances SET OnHandUnits=OnHandUnits+1000");throw new InvalidOperationException();});return Task.CompletedTask;});}finally{SecurityContext.Current=null;}Assert(db.Read(d=>d.Scalar<int>("SELECT COUNT(*) FROM SecurityOperationOwners WHERE OperationId=$op",("op",op)))==0);Assert(!accountService.AuditLog().Any(a=>a.OperationId==op));});
    await Test("访问审计记录成功和拒绝且不含密码令牌",async()=>{var audit=await Success<List<SecurityAudit>>("GET","/api/security/audit",token:adminToken);Assert(audit.Any(a=>a.Outcome=="Denied")&&audit.Any(a=>a.OperationId==receipt.OperationId));var json=JsonSerializer.Serialize(audit);Assert(!json.Contains(initial)&&!json.Contains(changed)&&!json.Contains(adminToken));});
    await Test("退出登录后会话不可复用",async()=>{await Success<JsonElement>("POST","/api/security/logout",new{},adminToken);Assert((await Send("GET","/api/orders",token:adminToken)).Code==401);});
    await Test("登录入口按来源 IP 限流",async()=>{bool limited=false;for(int i=0;i<65;i++){var r=await Send("POST","/api/security/login",new LoginRequest("x","wrong"));if(r.Code==429){limited=true;break;}}Assert(limited);});
}
finally{if(!process.HasExited){process.Kill(true);await process.WaitForExitAsync();}await errors;}
await File.WriteAllTextAsync(Path.Combine(output,"account-results.json"),JsonSerializer.Serialize(new{passed=failed==0,total=results.Count,failed,results},ErpJson.Options));
return failed==0?0:1;
