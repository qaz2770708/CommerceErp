using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using CommerceErp.Core;
using CommerceErp.Infrastructure;

var output=Path.GetFullPath(args[0]);var repo=Path.GetFullPath(args[1]);Directory.CreateDirectory(output);
int Number(string name,int fallback){var i=Array.IndexOf(args,name);return i<0?fallback:int.Parse(args[i+1]);}
var orders=Number("--orders",200);var concurrency=Number("--concurrency",8);
if(orders is <100 or >5000||concurrency is <1 or >64)throw new ArgumentException("orders 须为100–5000，concurrency须为1–64。");
var useSql=args.Contains("--sql");var data=Path.Combine(output,"data-"+Guid.NewGuid().ToString("N"));var provider=useSql?"SqlServer":"Sqlite";
var connection=useSql?Environment.GetEnvironmentVariable("ERP_SQL_TEST_CONNECTION"):null;
if(useSql)CommerceDatabase.InitializeSqlServerDemo(connection??throw new ArgumentException("须提供专用空测试库 ERP_SQL_TEST_CONNECTION。"),args[Array.IndexOf(args,"--sql")+1]);
var db=useSql?CommerceDatabase.SqlServer(connection!):new CommerceDatabase(data);var service=new OrderService(db);
var gateway=Guid.NewGuid().ToString("N")+Guid.NewGuid().ToString("N");var config=Path.Combine(output,"server.json");
await File.WriteAllTextAsync(config,JsonSerializer.Serialize(new{listenAddress="127.0.0.1",port=0,databaseProvider=provider,dataDirectory=data}));
Process? host=null;Task<string>? stderr=null;HttpClient? http=null;string? token=null;int httpAttempts=0,retries=0;var checks=new List<object>();var failed=0;
void Assert(bool b,string message="断言失败"){if(!b)throw new Exception(message);}
async Task Check(string name,Func<Task> a){try{await a();checks.Add(new{name,passed=true});Console.WriteLine("PASS "+name);}catch(Exception e){failed++;checks.Add(new{name,passed=false,error=e.GetType().Name+": "+e.Message});Console.WriteLine("FAIL "+name+": "+e.Message);}}
async Task Stop(){http?.Dispose();if(host is not null){if(!host.HasExited){host.Kill(true);await host.WaitForExitAsync();}if(stderr is not null)await stderr;host.Dispose();host=null;}}
async Task Start()
{
    var psi=new ProcessStartInfo("dotnet"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};psi.ArgumentList.Add(Path.Combine(repo,"src/CommerceErp.Api/bin/Release/net10.0/CommerceErp.Api.dll"));psi.ArgumentList.Add("--server");psi.ArgumentList.Add(config);psi.Environment["ERP_SESSION_TOKEN"]=gateway;if(useSql)psi.Environment["ERP_SQL_CONNECTION"]=connection!;
    host=Process.Start(psi)!;stderr=host.StandardError.ReadToEndAsync();using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(30));Uri? uri=null;
    while(await host.StandardOutput.ReadLineAsync(timeout.Token) is{} line)if(line.StartsWith("ERP_READY|")){uri=new(line[10..]);break;}
    if(uri is null)throw new Exception("容量测试服务未启动。");http=new(){BaseAddress=uri,Timeout=TimeSpan.FromSeconds(30)};http.DefaultRequestHeaders.Add("X-Erp-Session",gateway);
}
async Task<T> Send<T>(string path,object body,bool auth=true)
{
    for(int attempt=0;;attempt++)
    {
        using var req=new HttpRequestMessage(HttpMethod.Post,path){Content=JsonContent.Create(body,options:ErpJson.Options)};if(auth)req.Headers.Authorization=new("Bearer",token);
        Interlocked.Increment(ref httpAttempts);using var res=await http!.SendAsync(req);var text=await res.Content.ReadAsStringAsync();
        if((int)res.StatusCode==503&&attempt<8){Interlocked.Increment(ref retries);await Task.Delay(Math.Min(800,50*(attempt+1)));continue;}
        Assert(res.IsSuccessStatusCode,$"{path}: {(int)res.StatusCode} {text}");return JsonSerializer.Deserialize<T>(text,ErpJson.Options)!;
    }
}
var latency=new ConcurrentBag<double>();var results=new ConcurrentBag<OrderCommandResult>();var clock=new Stopwatch();
try
{
    await Start();await Send<AccountInfo>("/api/security/setup",new SetupAccountRequest("capacity-admin","合成容量测试","Capacity-Fixture-2026!"),false);token=(await Send<LoginResult>("/api/security/login",new LoginRequest("capacity-admin","Capacity-Fixture-2026!"),false)).Token;
    var initial=service.Inventory().Single(x=>x.SkuId=="sku-shirt");Assert(initial.Available==100);
    var beforeRequests=httpAttempts;clock.Start();
    await Check("并发HTTP重复接单不重复建单，热点SKU最多预占100件",async()=>
    {
        using var limit=new SemaphoreSlim(concurrency);await Task.WhenAll(Enumerable.Range(0,orders).Select(async i=>
        {
            await limit.WaitAsync();var sw=Stopwatch.StartNew();
            try
            {
                var b=SeedData.BaseOrder(Platform.Douyin);var source=b with{ExternalOrderId="LOAD-"+i,ShippingFee=0,OrderDiscount=0,PaidAmount=10,Lines=[b.Lines[0]with{Quantity=1,GoodsAmount=10,MerchantDiscount=0}]};
                var req=new ImportOrderRequest(Guid.NewGuid().ToString("N"),source);var pair=await Task.WhenAll(Send<OrderCommandResult>("/api/orders",req),Send<OrderCommandResult>("/api/orders",req));
                Assert(pair[0].OrderId==pair[1].OrderId&&pair.Count(x=>x.Replayed)==1);results.Add(pair[0]);latency.Add(sw.Elapsed.TotalMilliseconds);
            }
            finally{limit.Release();}
        }));
        Assert(results.Count==orders&&results.Select(r=>r.OrderId).Distinct().Count()==orders&&results.Count(r=>r.State==ReviewState.Approved)==100&&results.Count(r=>r.State==ReviewState.Exception)==orders-100);
        var balance=service.Inventory().Single(x=>x.SkuId=="sku-shirt");Assert(balance.Reserved==100&&balance.OnHand==100&&balance.Available==0&&service.List().Total==orders&&service.CheckConsistency().Passed);
    });
    clock.Stop();var loadRequests=httpAttempts-beforeRequests;var loadRetries=retries;
    await Check("并发重复取消只释放一次预占",async()=>
    {
        await Task.WhenAll(results.Where(x=>x.State==ReviewState.Approved).Take(10).Select(async order=>{var req=new OrderActionRequest(Guid.NewGuid().ToString("N"),order.Version,"容量测试取消");var r=await Task.WhenAll(Send<OrderCommandResult>("/api/orders/"+order.OrderId+"/cancel",req),Send<OrderCommandResult>("/api/orders/"+order.OrderId+"/cancel",req));Assert(r.Count(x=>x.Replayed)==1);}));
        var stock=service.Inventory().Single(x=>x.SkuId=="sku-shirt");Assert(stock.Reserved==90&&stock.Available==10&&stock.OnHand==100&&service.CheckConsistency().Passed);
    });
    await Check("提交回执丢失并重启API后原请求重试不重复补货",async()=>
    {
        var req=new ReceiveStockRequest(Guid.NewGuid().ToString("N"),3,"模拟提交后丢失回执");var before=service.Inventory().Single(x=>x.SkuId=="sku-shirt").OnHand;
        _=await Send<StockCommandResult>("/api/inventory/sku-shirt/receive",req);await Stop();await Start();var replay=await Send<StockCommandResult>("/api/inventory/sku-shirt/receive",req);
        Assert(replay.Replayed&&service.Inventory().Single(x=>x.SkuId=="sku-shirt").OnHand==before+3&&service.CheckConsistency().Passed);
    });
    await Check("服务重启后业务事件与任务记录仍可查询",()=>
    {var tasks=new WorkTaskService(db);Assert(tasks.Summary().Total>=orders&&tasks.List(pageSize:1).Total==tasks.Summary().Total);return Task.CompletedTask;});
    var sorted=latency.Order().ToArray();double? Percentile(double p)=>sorted.Length==0?null:sorted[(int)Math.Ceiling(sorted.Length*p)-1];
    await File.WriteAllTextAsync(Path.Combine(output,"capacity-report.json"),JsonSerializer.Serialize(new
    {
        generatedAtUtc=DateTimeOffset.UtcNow,provider,scope="Synthetic_Localhost_SingleApi_HotSku_CorrectnessBaseline",productionCapacityAccepted=false,
        sqlServerExecuted=useSql,logicalOrders=orders,completedOrders=results.Count,logicalConcurrency=concurrency,parallelRequestsPerOrder=2,httpLoadAttempts=loadRequests,retry503=loadRetries,
        elapsedSeconds=clock.Elapsed.TotalSeconds,completedLogicalOrdersPerSecond=results.Count/Math.Max(.001,clock.Elapsed.TotalSeconds),httpAttemptsPerSecond=loadRequests/Math.Max(.001,clock.Elapsed.TotalSeconds),
        logicalPairLatencyMilliseconds=new{p50=Percentile(.50),p95=Percentile(.95),p99=Percentile(.99),max=sorted.LastOrDefault()},latencyIncludes="Admission and both duplicate HTTP responses; excludes waiting for client concurrency slot",
        environment=new{os=System.Runtime.InteropServices.RuntimeInformation.OSDescription,runtime=System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,logicalProcessors=Environment.ProcessorCount},
        limitations="Single hot SKU and synthetic orders; excludes real platform latency, long-duration soak, multi-host networking, warehouse/scanning workload and production hardware acceptance."
    },ErpJson.Options));
}
finally{await Stop();}
await File.WriteAllTextAsync(Path.Combine(output,"capacity-results.json"),JsonSerializer.Serialize(new{passed=failed==0,total=checks.Count,failed,checks},ErpJson.Options));return failed==0?0:1;
