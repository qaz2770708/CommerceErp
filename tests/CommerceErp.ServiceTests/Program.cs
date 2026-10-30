using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CommerceErp.Core;
using CommerceErp.Desktop;
using CommerceErp.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

var output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
var repository = Path.GetFullPath(args[1]); var root = Path.Combine(output, "data-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
var results = new List<object>(); var failures = 0; var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
var processes = new List<Process>();
string accountToken="";const string accountPassword="ServiceFixture-2026!";
void Assert(bool ok, string message="断言失败") { if(!ok)throw new InvalidOperationException(message); }
async Task Test(string name,Func<Task> action) { try { await action(); results.Add(new {name,passed=true}); Console.WriteLine("PASS "+name); } catch(Exception e){ failures++;results.Add(new{name,passed=false,error=e.Message});Console.WriteLine("FAIL "+name+" "+e.Message);} }
async Task Throws<T>(Func<Task> action) where T:Exception { try{await action();}catch(T){return;}throw new InvalidOperationException("未发生预期异常 "+typeof(T).Name); }
async Task<(Process Process,Uri Address)> Start(string name,string data)
{
    var config=Path.Combine(root,name+".json"); await File.WriteAllTextAsync(config,JsonSerializer.Serialize(new {listenAddress="127.0.0.1",port=0,databaseProvider="Sqlite",dataDirectory=data}));
    var start=new ProcessStartInfo("dotnet"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
    start.ArgumentList.Add(Path.Combine(repository,"src/CommerceErp.Api/bin/Release/net10.0/CommerceErp.Api.dll"));start.ArgumentList.Add("--server");start.ArgumentList.Add(config);
    start.Environment["ERP_SESSION_TOKEN"]=token;start.Environment["ERP_PARENT_PROCESS_ID"]="2147483647";
    var process=Process.Start(start)!;processes.Add(process);using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(25));
    while(await process.StandardOutput.ReadLineAsync(timeout.Token) is { } line) if(line.StartsWith("ERP_READY|"))return(process,new Uri(line[10..]));
    throw new InvalidOperationException("服务未就绪："+await process.StandardError.ReadToEndAsync());
}
HttpClient Http(Uri url) { var h=new HttpClient {BaseAddress=url};h.DefaultRequestHeaders.Add("X-Erp-Session",token);if(accountToken.Length>0)h.DefaultRequestHeaders.Authorization=new("Bearer",accountToken);return h; }
try
{
    var databasePath=Path.Combine(root,"shared");var server=await Start("one",databasePath);using var http=Http(server.Address);
    await http.PostAsJsonAsync("/api/security/setup",new SetupAccountRequest("test-admin","测试管理员",accountPassword),ErpJson.Options);
    accountToken=(await (await http.PostAsJsonAsync("/api/security/login",new LoginRequest("test-admin",accountPassword),ErpJson.Options)).Content.ReadFromJsonAsync<LoginResult>(ErpJson.Options))!.Token;
    http.DefaultRequestHeaders.Authorization=new("Bearer",accountToken);
    var health=(await http.GetFromJsonAsync<ServiceHealth>("/api/health",ErpJson.Options))!;
    await Test("独立进程忽略桌面父进程并报告数据库身份",async()=>{Assert(!server.Process.HasExited&&health.Mode=="SharedServiceDemo"&&health.SchemaVersion==10);Assert((await http.GetFromJsonAsync<BootstrapData>("/api/bootstrap",ErpJson.Options))!.Mode=="SharedServiceDemo");});
    await Test("无凭证访问被拒绝",async()=>{using var c=new HttpClient();Assert((await c.GetAsync(new Uri(server.Address,"/api/health"))).StatusCode==HttpStatusCode.Unauthorized);});
    await Test("共享服务禁止客户端关闭",async()=>{Assert((await http.PostAsync("/api/shutdown",null)).StatusCode==HttpStatusCode.NotFound);});
    await Test("数据库身份不符在写入前拒绝",async()=>{using var c=Http(server.Address);c.DefaultRequestHeaders.Add("X-Erp-Database",Guid.NewGuid().ToString("N"));Assert((await c.GetAsync("/api/inventory")).StatusCode==HttpStatusCode.PreconditionFailed);});
    await using var a=new LocalApiClient();await using var b=new LocalApiClient();
    await a.StartRemoteAsync(server.Address.AbsoluteUri,token,Path.Combine(root,"client-a"));await b.StartRemoteAsync(server.Address.AbsoluteUri,token,Path.Combine(root,"client-b"));
    await a.LoginAsync("test-admin",accountPassword);await b.LoginAsync("test-admin",accountPassword);
    var before=(await a.GetAsync<List<InventoryRow>>("/api/inventory")).Single(x=>x.SkuId=="sku-shirt").OnHand;
    await Test("两客户端并行补货合并余额",async()=>{
        await Task.WhenAll(Enumerable.Range(0,10).Select(i=>(i%2==0?a:b).PostCommandAsync<JsonElement>("/api/inventory/sku-shirt/receive",new{quantity=1,reason="共享测试"+i})));
        Assert((await b.GetAsync<List<InventoryRow>>("/api/inventory")).Single(x=>x.SkuId=="sku-shirt").OnHand==before+10);
    });
    await Test("多个服务实例共享同一数据库",async()=>{
        var second=await Start("two",databasePath);using var h=Http(second.Address);
        Assert((await h.GetFromJsonAsync<ServiceHealth>("/api/health",ErpJson.Options))!.InstanceId==health.InstanceId);
        var op=new ReceiveStockRequest(Guid.NewGuid().ToString("N"),2,"跨实例重复提交");
        var responses=await Task.WhenAll(http.PostAsJsonAsync("/api/inventory/sku-shirt/receive",op,ErpJson.Options),h.PostAsJsonAsync("/api/inventory/sku-shirt/receive",op,ErpJson.Options));
        foreach(var response in responses){Assert(response.IsSuccessStatusCode);response.Dispose();}
        Assert((await a.GetAsync<List<InventoryRow>>("/api/inventory")).Single(x=>x.SkuId=="sku-shirt").OnHand==before+12);
        second.Process.Kill(true);await second.Process.WaitForExitAsync();
    });
    await Test("关闭远程客户端不会关闭服务",async()=>{var client=new LocalApiClient();await client.StartRemoteAsync(server.Address.AbsoluteUri,token,Path.Combine(root,"close"));await client.DisposeAsync();Assert((await http.GetAsync("/api/health")).IsSuccessStatusCode);});
    await Test("相同客户端档案禁止并开",async()=>{await using var other=new LocalApiClient();await Throws<InvalidOperationException>(()=>other.StartRemoteAsync(server.Address.AbsoluteUri,token,Path.Combine(root,"client-a")));});
    var oldFile=Path.Combine(repository,"tests/Fixtures/stage5-v4.db");
    await Test("第五阶段快照升级且源文件不变",()=>{
        var hash=SHA256.HashData(File.ReadAllBytes(oldFile));var snapshot=CommerceDatabase.CreateVerifiedSnapshot(oldFile,Path.Combine(root,"snapshot"));
        var upgraded=new CommerceDatabase(Path.GetDirectoryName(snapshot)!);Assert(upgraded.InstanceId.Length==32&&new OrderService(upgraded).FinanceExports().Count>0);
        Assert(hash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(oldFile))));return Task.CompletedTask;
    });
    await Test("快照不覆盖已有目录",()=>Throws<InvalidDataException>(()=>{CommerceDatabase.CreateVerifiedSnapshot(oldFile,Path.Combine(root,"snapshot"));return Task.CompletedTask;}));
    await Test("迁移源锁拒绝正在运行的服务",()=>Throws<IOException>(()=>{using var lease=CommerceDatabase.LockMigrationSource(Path.Combine(databasePath,"commerce.db"));return Task.CompletedTask;}));
    await Test("迁移源拒绝待重试操作",()=>Throws<InvalidDataException>(()=>{
        var path=Path.Combine(root,"pending-source");Directory.CreateDirectory(path);File.WriteAllText(Path.Combine(path,"pending-commands.json"),"{\"pending\":{}}");
        using var lease=CommerceDatabase.LockMigrationSource(Path.Combine(path,"commerce.db"));return Task.CompletedTask;
    }));
    await Test("数据库身份在重启后保持",async()=>{
        server.Process.Kill(true);await server.Process.WaitForExitAsync();server=await Start("restart",databasePath);
        using var h=Http(server.Address);Assert((await h.GetFromJsonAsync<ServiceHealth>("/api/health",ErpJson.Options))!.InstanceId==health.InstanceId);
        Assert((await h.GetFromJsonAsync<List<InventoryRow>>("/api/inventory",ErpJson.Options))!.Single(x=>x.SkuId=="sku-shirt").OnHand==before+12);
    });
    var replacement=await Start("replacement",Path.Combine(root,"other-db"));
    await Test("切换数据库拒绝复用旧档案",async()=>{await a.DisposeAsync();await using var client=new LocalApiClient();await Throws<InvalidDataException>(()=>client.StartRemoteAsync(replacement.Address.AbsoluteUri,token,Path.Combine(root,"client-a")));});
    await Test("未绑定的旧待重试记录禁止发送到远端",async()=>{
        var profile=Path.Combine(root,"unbound");Directory.CreateDirectory(profile);await File.WriteAllTextAsync(Path.Combine(profile,"pending-commands.json"),"{\"old\":{\"path\":\"/api/inventory/sku-shirt/receive\",\"json\":\"{}\"}}");
        await using var client=new LocalApiClient();await Throws<InvalidDataException>(()=>client.StartRemoteAsync(server.Address.AbsoluteUri,token,profile));
    });
    await Test("远端禁止明文 HTTP、嵌入凭证和子路径",()=>{
        foreach(var url in new[]{"http://192.0.2.10:5088","https://user:pass@example.com","https://example.com/api","https://example.com/?token=x"})
        {try{ClientConnection.ValidateAddress(url);throw new Exception("接受了非法地址");}catch(InvalidDataException){}}
        return Task.CompletedTask;
    });
    await Test("默认配置不保存访问令牌",()=>{var c=ClientConnection.Remote(server.Address.AbsoluteUri,token);Assert(c.ProtectedToken==""&&!JsonSerializer.Serialize(c).Contains(token));return Task.CompletedTask;});
    await Test("Windows 加密失败时不降级为明文保存",()=>{try {var c=ClientConnection.Remote(server.Address.AbsoluteUri,token,true);Assert(c.Token()==token&&!JsonSerializer.Serialize(c).Contains(token));} catch(CryptographicException){Assert(ClientConnection.Remote(server.Address.AbsoluteUri,token).ProtectedToken=="");}return Task.CompletedTask;});
    await Test("数据库连接拒绝系统库及跳过证书",()=>{
        foreach(var cs in new[]{"Server=localhost;Database=master;Integrated Security=True","Server=localhost;Database=erp;Encrypt=False","Server=localhost;Database=erp;TrustServerCertificate=True"})
        {try{CommerceDatabase.ValidateSqlConnection(cs);throw new Exception("接受了危险连接");}catch(InvalidDataException){}}return Task.CompletedTask;
    });
    // A real HTTP relay injects lost acknowledgements and authentication outages after startup.
    var relayBuilder=WebApplication.CreateBuilder();relayBuilder.Logging.ClearProviders();relayBuilder.WebHost.ConfigureKestrel(k=>k.Listen(IPAddress.Loopback,0));
    await using var relay=relayBuilder.Build();using var forward=new HttpClient();var target=server.Address;var failure="";
    relay.Run(async context=>{
        if(failure=="auth"&&context.Request.Method=="POST"){context.Response.StatusCode=401;return;}
        using var request=new HttpRequestMessage(new HttpMethod(context.Request.Method),new Uri(target,context.Request.Path+context.Request.QueryString));
        request.Headers.Add("X-Erp-Session",token);if(context.Request.Headers.TryGetValue("Authorization",out var auth))request.Headers.Add("Authorization",auth.ToString());if(context.Request.Headers.TryGetValue("X-Erp-Database",out var identity))request.Headers.Add("X-Erp-Database",identity.ToString());
        if(context.Request.Method=="POST") {request.Content=new StreamContent(context.Request.Body);request.Content.Headers.ContentType=new("application/json");}
        using var response=await forward.SendAsync(request);
        if(failure=="lost"&&context.Request.Method=="POST"){context.Response.StatusCode=503;await context.Response.WriteAsync("{}");return;}
        context.Response.StatusCode=(int)response.StatusCode;context.Response.ContentType="application/json";await response.Content.CopyToAsync(context.Response.Body);
    });
    await relay.StartAsync();var relayUrl=relay.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    var journal=Path.Combine(root,"relay-client");var relayClient=new LocalApiClient();await relayClient.StartRemoteAsync(relayUrl,token,journal);await relayClient.LoginAsync("test-admin",accountPassword);
    await Test("提交成功但回执丢失保留待重试记录",async()=>{failure="lost";await Throws<BusinessRuleException>(()=>relayClient.PostCommandAsync<JsonElement>("/api/inventory/sku-shirt/receive",new{quantity=3,reason="丢失回执"}));Assert(relayClient.PendingCount==1);});
    await Test("重开客户端重试不会重复补货",async()=>{
        failure="";await relayClient.DisposeAsync();relayClient=new();await relayClient.StartRemoteAsync(relayUrl,token,journal);await relayClient.LoginAsync("test-admin",accountPassword);
        Assert(await relayClient.RetryPendingAsync()==1&&relayClient.PendingCount==0);
        Assert((await relayClient.GetAsync<List<InventoryRow>>("/api/inventory")).Single(x=>x.SkuId=="sku-shirt").OnHand==before+15);
    });
    await Test("凭证失效保留操作并支持恢复",async()=>{
        failure="auth";await Throws<BusinessRuleException>(()=>relayClient.PostCommandAsync<JsonElement>("/api/inventory/sku-shirt/receive",new{quantity=4,reason="认证恢复"}));Assert(relayClient.PendingCount==1);
        failure="";Assert(await relayClient.RetryPendingAsync()==1);
    });
    await Test("运行中切换数据库阻止重试并保留记录",async()=>{
        target=replacement.Address;await Throws<BusinessRuleException>(()=>relayClient.PostCommandAsync<JsonElement>("/api/inventory/sku-shirt/receive",new{quantity=5,reason="身份防护"}));Assert(relayClient.PendingCount==1);
        target=server.Address;Assert(await relayClient.RetryPendingAsync()==1);
    });
    await relayClient.DisposeAsync();await relay.StopAsync();
    await Test("共享操作后各业务一致性检查通过",async()=>{
        using var h=Http(server.Address);
        foreach(var endpoint in new[]{"/api/business/consistency","/api/warehouse/audit","/api/after-sales/audit","/api/finance/audit"})
        {var result=await h.GetFromJsonAsync<JsonElement>(endpoint);Assert(result.GetProperty("passed").GetBoolean(),endpoint);}
    });
}
finally
{
    foreach(var process in processes){if(!process.HasExited){process.Kill(true);await process.WaitForExitAsync();}process.Dispose();}
    await File.WriteAllTextAsync(Path.Combine(output,"service-results.json"),JsonSerializer.Serialize(new{passed=failures==0,total=results.Count,failures,sqlServerIntegration="NotRun_NoInstance",checks=results},new JsonSerializerOptions{WriteIndented=true}));
}
return failures==0?0:1;
