using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using CommerceErp.Core;
using CommerceErp.Infrastructure;

string Op()=>Guid.NewGuid().ToString("N");
if(args[0] is "--crash-claim" or "--crash-send")
{
    var database=new CommerceDatabase(args[1]);var crash=args[0]=="--crash-claim"?"after-claim":"after-send";
    await new OutboxProcessor(database,[new FileHandler(args[2])],fault:p=>{if(p==crash)Environment.Exit(74);}).ProcessNextAsync();return 2;
}
var output=Path.GetFullPath(args[0]);Directory.CreateDirectory(Path.GetDirectoryName(output)!);var root=Path.Combine(Path.GetTempPath(),"CommerceErp.TaskTests",Op());Directory.CreateDirectory(root);
var results=new List<object>();var failed=0;
void Assert(bool condition,string message="断言失败"){if(!condition)throw new Exception(message);}
void Throws<T>(Action action)where T:Exception{try{action();}catch(T){return;}throw new Exception("预期异常未发生");}
async Task Test(string name,Func<Task> action){try{await action();results.Add(new{name,passed=true});Console.WriteLine("PASS "+name);}catch(Exception e){failed++;results.Add(new{name,passed=false,error=e.ToString()});Console.WriteLine("FAIL "+name+": "+e);}}
Task Sync(Action a){a();return Task.CompletedTask;}
(CommerceDatabase Db,OrderService Service,WorkTaskService Tasks) Fresh(){var d=new CommerceDatabase(Path.Combine(root,Op()));return(d,new(d),new(d));}
string Event(CommerceDatabase db,string aggregate="stock:sku-shirt",long version=1,string type="StockReceived")
{var id=Op();db.Write(d=>d.Execute("INSERT INTO Outbox(Id,TenantId,AggregateId,AggregateVersion,EventType,PayloadJson,CreatedAt,UpdatedAt,NextAttemptAt) VALUES($id,$tenant,$aggregate,$version,$type,'{}',$now,$now,$now)",("id",id),("tenant",SeedData.Tenant),("aggregate",aggregate),("version",version),("type",type),("now",DateTimeOffset.UtcNow.ToString("O"))));return id;}
WorkTaskRow Read(WorkTaskService s,string id)=>s.Details(WorkTaskKind.BusinessEvent,id).Task;
RetryWorkTaskRequest Retry(WorkTaskService s,string id)=>new(Op(),Read(s,id).Version,"测试员","核查后重试");
async Task Crash(string mode,CommerceDatabase db,string receipt)
{var psi=new ProcessStartInfo("dotnet"){UseShellExecute=false,CreateNoWindow=true};psi.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);psi.ArgumentList.Add(mode);psi.ArgumentList.Add(Path.GetDirectoryName(db.DatabasePath)!);psi.ArgumentList.Add(receipt);using var p=Process.Start(psi)!;using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(20));await p.WaitForExitAsync(timeout.Token);Assert(p.ExitCode==74);}

await Test("schema6升级保留账号订单事件与库身份，重复打开不重置",()=>Sync(()=>
{
    var(db,s,t)=Fresh();var a=new AccountService(db).Setup(new("fixture-admin","合成管理员","Fixture-Only-2026!"));s.Import(new(Op(),SeedData.BaseOrder(Platform.Douyin)));var identity=db.InstanceId;var before=s.List().Total;var events=t.Summary().Total;
    // Build an isolated exact pre-007 layout, never alter a user's database.
    db.Write(d=>{d.Execute("ALTER TABLE InventoryLedger DROP COLUMN StockDocumentId;DROP TABLE InventoryReversals;DROP TABLE InventoryMovements;DROP TABLE InventoryDocumentHistory;DROP TABLE InventoryDocuments;DROP TABLE PlatformOrderLines;DROP TABLE OpeningPolicyChanges;DROP TABLE OpeningConfiguration;DROP TABLE OpeningSkuClaims; DROP TABLE OpeningDocuments; DROP TABLE MasterHistory; DROP TABLE MasterMappingState; DROP TABLE MasterShopData; DROP TABLE MasterSkuData; DROP TABLE MasterWorkspace; DROP TABLE WorkerHealth; ALTER TABLE DeliveryJobs DROP COLUMN CycleAttempts; DROP TABLE TaskActions; DROP TABLE OutboxAttempts; DROP INDEX IX_Outbox_Due;");foreach(var column in new[]{"Version","Attempts","CycleAttempts","NextAttemptAt","LeaseToken","LeaseUntil","LastCode","Receipt","UpdatedAt"})d.Execute("ALTER TABLE Outbox DROP COLUMN "+column);return d.Execute("DELETE FROM SchemaMigrations WHERE Version>=7");});
    var reopened=new CommerceDatabase(Path.GetDirectoryName(db.DatabasePath)!);Assert(reopened.InstanceId==identity&&new OrderService(reopened).List().Total==before&&new WorkTaskService(reopened).Summary().Total==events&&new AccountService(reopened).Accounts().Single().Id==a.Id);_=new CommerceDatabase(Path.GetDirectoryName(db.DatabasePath)!);Assert(reopened.Read(d=>d.Scalar<int>("SELECT MAX(Version) FROM SchemaMigrations"))==10);
}));
await Test("业务提交与事件同事务，回滚不留下待处理任务",()=>Sync(()=>
{var(db,_,t)=Fresh();var s=new OrderService(db,p=>{if(p=="before-commit")throw new IOException();});Throws<IOException>(()=>s.Receive("sku-shirt",new(Op(),1,"回滚测试")));Assert(t.Summary().Total==0);}));
await Test("无连接器明确阻断且不改变库存",async()=>
{var(db,s,t)=Fresh();var id=Event(db);var before=s.Inventory();Assert(await new OutboxProcessor(db,[]).ProcessNextAsync());var row=Read(t,id);Assert(row.State=="Blocked"&&row.CanRetry&&row.Attempts==1&&t.Summary().Attention==1);Assert(JsonSerializer.Serialize(before)==JsonSerializer.Serialize(s.Inventory()));});
await Test("同一业务对象前序未确认时后续事件不越过",async()=>
{var(db,_,t)=Fresh();var first=Event(db);var second=Event(db,version:2);var processor=new OutboxProcessor(db,[]);await processor.ProcessNextAsync();Assert(!await processor.ProcessNextAsync()&&Read(t,first).State=="Blocked"&&Read(t,second).Attempts==0);t.Retry(WorkTaskKind.BusinessEvent,first,Retry(t,first));var h=new MemoryHandler();var active=new OutboxProcessor(db,[h]);Assert(await active.ProcessNextAsync()&&await active.ProcessNextAsync());Assert(h.Applied.SequenceEqual(new[]{first,second}));});
await Test("多个消费者并发领取同一任务只产生一次有效处理",async()=>
{var(db,_,t)=Fresh();var id=Event(db);var h=new MemoryHandler();await Task.WhenAll(Enumerable.Range(0,12).Select(_=>Task.Run(()=>new OutboxProcessor(db,[h]).ProcessNextAsync())));Assert(Read(t,id).State=="Confirmed"&&h.Applied.Count==1&&t.Details(WorkTaskKind.BusinessEvent,id).History.Count==1);});
await Test("查询确认已处理时不再次发送",async()=>
{var(db,_,t)=Fresh();var id=Event(db);var h=new StubHandler(EventOutcome.Confirmed,EventOutcome.Rejected);await new OutboxProcessor(db,[h]).ProcessNextAsync();Assert(Read(t,id).State=="Confirmed"&&h.Sends==0);});
await Test("重复处理器配置阻断，不选择任意处理器",async()=>
{var(db,_,t)=Fresh();var id=Event(db);await new OutboxProcessor(db,[new MemoryHandler(),new MemoryHandler()]).ProcessNextAsync();Assert(Read(t,id).State=="Blocked"&&Read(t,id).Message.Contains("配置冲突"));});
await Test("缺少回执不能标记确认成功",async()=>
{var(db,_,t)=Fresh();var id=Event(db);await new OutboxProcessor(db,[new EmptyReceiptHandler()]).ProcessNextAsync();Assert(Read(t,id).State=="Unknown"&&Read(t,id).NextAttemptAt is not null);});
await Test("异常消息不向任务详情暴露平台凭证或原始载荷",async()=>
{var(db,_,t)=Fresh();var id=Event(db);await new OutboxProcessor(db,[new ThrowHandler()]).ProcessNextAsync();Assert(Read(t,id).State=="Unknown"&&!JsonSerializer.Serialize(t.Details(WorkTaskKind.BusinessEvent,id)).Contains("SECRET"));});
await Test("暂时失败按退避计划重试并在五次后停止",async()=>
{var(db,_,t)=Fresh();var id=Event(db);var clock=new TestClock();var p=new OutboxProcessor(db,[new StubHandler(EventOutcome.Retryable,EventOutcome.Rejected)],clock);for(int i=1;i<=5;i++){Assert(await p.ProcessNextAsync());Assert(Read(t,id).Attempts==i);if(i<5){Assert(Read(t,id).State=="RetryWait"&&!await p.ProcessNextAsync());clock.Advance(2000);}}Assert(Read(t,id).State=="Failed"&&Read(t,id).NextAttemptAt is null&&!await p.ProcessNextAsync());});
await Test("结果不明达到上限仍保留未知状态",async()=>
{var(db,_,t)=Fresh();var id=Event(db);var clock=new TestClock();var p=new OutboxProcessor(db,[new StubHandler(EventOutcome.Unknown,EventOutcome.Rejected)],clock);for(int i=0;i<5;i++){await p.ProcessNextAsync();clock.Advance(2000);}Assert(Read(t,id).State=="Unknown"&&Read(t,id).NextAttemptAt is null&&Read(t,id).CanRetry);});
await Test("人工重试保留任务ID与总次数，同操作号不重复留痕",async()=>
{var(db,_,t)=Fresh();var id=Event(db);await new OutboxProcessor(db,[]).ProcessNextAsync();var req=Retry(t,id);var result=t.Retry(WorkTaskKind.BusinessEvent,id,req);var again=t.Retry(WorkTaskKind.BusinessEvent,id,req);Assert(result.Id==id&&again.Replayed&&Read(t,id).Attempts==1&&t.Details(WorkTaskKind.BusinessEvent,id).History.Count(x=>x.Action=="ManualRetry")==1);Throws<ConflictException>(()=>t.Retry(WorkTaskKind.BusinessEvent,id,req with{Reason="改变请求"}));});
await Test("旧版本重试与空原因被拒绝",async()=>
{var(db,_,t)=Fresh();var id=Event(db);await new OutboxProcessor(db,[]).ProcessNextAsync();Throws<ConflictException>(()=>t.Retry(WorkTaskKind.BusinessEvent,id,Retry(t,id)with{ExpectedVersion=1}));Throws<BusinessRuleException>(()=>t.Retry(WorkTaskKind.BusinessEvent,id,Retry(t,id)with{Reason=""}));});
await Test("已确认或已排队的任务不能用新操作号重复排队",async()=>
{var(db,_,t)=Fresh();var id=Event(db);Throws<ConflictException>(()=>t.Retry(WorkTaskKind.BusinessEvent,id,Retry(t,id)));await new OutboxProcessor(db,[new MemoryHandler()]).ProcessNextAsync();Throws<ConflictException>(()=>t.Retry(WorkTaskKind.BusinessEvent,id,Retry(t,id)));});
await Test("领取后进程退出，重开数据库在租约到期后接续",async()=>
{var(db,_,t)=Fresh();var id=Event(db);await Crash("--crash-claim",db,Path.Combine(root,Op()+".json"));Assert(Read(t,id).State=="Processing");var clock=new TestClock();Assert(!await new OutboxProcessor(db,[new MemoryHandler()],clock).ProcessNextAsync());clock.Advance(61);Assert(await new OutboxProcessor(new CommerceDatabase(Path.GetDirectoryName(db.DatabasePath)!),[new MemoryHandler()],clock).ProcessNextAsync());Assert(Read(t,id).State=="Confirmed"&&Read(t,id).Attempts==2&&t.Details(WorkTaskKind.BusinessEvent,id).History.Any(h=>h.Action=="LeaseExpired"));});
await Test("外部已受理但本地回执前退出，恢复先回查而不重复发送",async()=>
{var(db,_,t)=Fresh();var id=Event(db);var file=Path.Combine(root,Op()+".json");await Crash("--crash-send",db,file);var clock=new TestClock();clock.Advance(61);var h=new FileHandler(file);await new OutboxProcessor(db,[h],clock).ProcessNextAsync();Assert(Read(t,id).State=="Confirmed"&&h.Sends==0&&JsonSerializer.Deserialize<List<string>>(File.ReadAllText(file))!.Count==1);});
await Test("旧消费者晚到的结果不能覆盖新租约已确认结果",async()=>
{var(db,_,t)=Fresh();var id=Event(db);var clock=new TestClock();var slow=new ControlledHandler();var old=new OutboxProcessor(db,[slow],clock).ProcessNextAsync();await slow.Entered.Task;clock.Advance(61);await new OutboxProcessor(db,[new MemoryHandler()],clock).ProcessNextAsync();slow.Release.SetResult(new(EventOutcome.Rejected));await old;Assert(Read(t,id).State=="Confirmed"&&Read(t,id).Attempts==2);});
await Test("处理回执事务中断后回滚，恢复仍先回查",async()=>
{var(db,_,t)=Fresh();var id=Event(db);var h=new MemoryHandler();try{await new OutboxProcessor(db,[h],fault:p=>{if(p=="before-finish-commit")throw new IOException();}).ProcessNextAsync();}catch(IOException){}Assert(Read(t,id).State=="Processing");var clock=new TestClock();clock.Advance(61);await new OutboxProcessor(db,[h],clock).ProcessNextAsync();Assert(Read(t,id).State=="Confirmed"&&h.Applied.Count==1);});
await Test("处理历史只显示最近一百条并保持可读时间顺序",()=>Sync(()=>
{var(db,_,t)=Fresh();var id=Event(db);db.Write(d=>{for(var i=0;i<110;i++)d.Execute("INSERT INTO OutboxAttempts VALUES($lease,$id,$attempt,'Unknown',$at,$at)",("lease",Op()),("id",id),("attempt",i+1),("at",DateTimeOffset.UtcNow.AddSeconds(i).ToString("O")));return true;});var history=t.Details(WorkTaskKind.BusinessEvent,id).History;Assert(history.Count==100&&history[0].StartedAt.CompareTo(history[^1].StartedAt)>0);}));
await Test("任务分页与输入校验",()=>Sync(()=>
{var(db,_,t)=Fresh();Event(db);Event(db,"stock:sku-mug");Assert(t.List(pageSize:1).Total==2&&t.List(pageSize:1).Items.Count==1&&t.Summary().Waiting==2);Throws<BusinessRuleException>(()=>t.List(state:"invalid"));Throws<BusinessRuleException>(()=>t.List(page:0));}));
await File.WriteAllTextAsync(output,JsonSerializer.Serialize(new{passed=failed==0,total=results.Count,failed,results},ErpJson.Options));return failed==0?0:1;

sealed class TestClock:TimeProvider{private DateTimeOffset _now=DateTimeOffset.UtcNow;public override DateTimeOffset GetUtcNow()=>_now;public void Advance(int seconds)=>_now=_now.AddSeconds(seconds);}
sealed class MemoryHandler:IOutboxHandler
{
    public List<string> Applied{get;}=[];private readonly object _gate=new();public bool CanHandle(string type)=>true;
    public Task<EventDeliveryResult> QueryAsync(OutboxEvent e,CancellationToken ct){lock(_gate)return Task.FromResult(new EventDeliveryResult(Applied.Contains(e.Id)?EventOutcome.Confirmed:EventOutcome.NotFound,Applied.Contains(e.Id)?"receipt-"+e.Id:""));}
    public Task<EventDeliveryResult> SendAsync(OutboxEvent e,CancellationToken ct){lock(_gate){if(!Applied.Contains(e.Id))Applied.Add(e.Id);return Task.FromResult(new EventDeliveryResult(EventOutcome.Confirmed,"receipt-"+e.Id));}}
}
sealed class StubHandler(EventOutcome query,EventOutcome send):IOutboxHandler
{public int Sends;public bool CanHandle(string type)=>true;public Task<EventDeliveryResult> QueryAsync(OutboxEvent e,CancellationToken ct)=>Task.FromResult(new EventDeliveryResult(query,"receipt"));public Task<EventDeliveryResult> SendAsync(OutboxEvent e,CancellationToken ct){Sends++;return Task.FromResult(new EventDeliveryResult(send,"receipt"));}}
sealed class EmptyReceiptHandler:IOutboxHandler
{public bool CanHandle(string type)=>true;public Task<EventDeliveryResult> QueryAsync(OutboxEvent e,CancellationToken ct)=>Task.FromResult(new EventDeliveryResult(EventOutcome.Confirmed));public Task<EventDeliveryResult> SendAsync(OutboxEvent e,CancellationToken ct)=>throw new Exception();}
sealed class ThrowHandler:IOutboxHandler
{public bool CanHandle(string type)=>true;public Task<EventDeliveryResult> QueryAsync(OutboxEvent e,CancellationToken ct)=>throw new IOException("SECRET token and personal payload");public Task<EventDeliveryResult> SendAsync(OutboxEvent e,CancellationToken ct)=>throw new Exception();}
sealed class ControlledHandler:IOutboxHandler
{public TaskCompletionSource Entered{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);public TaskCompletionSource<EventDeliveryResult> Release{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);public bool CanHandle(string type)=>true;public Task<EventDeliveryResult> QueryAsync(OutboxEvent e,CancellationToken ct){Entered.TrySetResult();return Release.Task;}public Task<EventDeliveryResult> SendAsync(OutboxEvent e,CancellationToken ct)=>throw new Exception();}
sealed class FileHandler(string file):IOutboxHandler
{public int Sends;public bool CanHandle(string type)=>true;private List<string> Read()=>File.Exists(file)?JsonSerializer.Deserialize<List<string>>(File.ReadAllText(file))!:[];public Task<EventDeliveryResult> QueryAsync(OutboxEvent e,CancellationToken ct)=>Task.FromResult(new EventDeliveryResult(Read().Contains(e.Id)?EventOutcome.Confirmed:EventOutcome.NotFound,"receipt-"+e.Id));public Task<EventDeliveryResult> SendAsync(OutboxEvent e,CancellationToken ct){var ids=Read();if(!ids.Contains(e.Id))ids.Add(e.Id);File.WriteAllText(file,JsonSerializer.Serialize(ids));Sends++;return Task.FromResult(new EventDeliveryResult(EventOutcome.Confirmed,"receipt-"+e.Id));}}
