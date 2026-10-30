using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using CommerceErp.Core;
using CommerceErp.Infrastructure;
using static CommerceErp.Infrastructure.CommerceDatabase;

string Op()=>Guid.NewGuid().ToString("N");
BillInput Input(string content,string? batch=null,Platform platform=Platform.Douyin)=>new(platform,SeedData.Shop(platform),batch??Op(),"合成账单.csv",content,new(),new(){{"收入",BillKind.Sale},{"退款",BillKind.Refund},{"平台费",BillKind.PlatformFee},{"运费",BillKind.Freight}});
string Csv(params string[] rows)=>"流水号,订单号,类型,金额,发生时间,币种,售后编号\r\n"+string.Join("\r\n",rows)+"\r\n";
string Line(string external,string kind="收入",decimal amount=10,string? tx=null,string sourceCase="",string time="2026-10-01T12:00:00+08:00")=>$"{tx??Op()},{external},{kind},{amount.ToString(System.Globalization.CultureInfo.InvariantCulture)},{time},CNY,{sourceCase}";
if(args.Length==2&&args[0]=="--crash-import")
{var s=new OrderService(new CommerceDatabase(args[1]),p=>{if(p=="finance-after-row")Environment.Exit(75);});s.ImportBill(new(Op(),Input(Csv(Line("X"),Line("Y"))),"测试员","中断测试"));return 2;}
var root=Path.Combine(Path.GetTempPath(),"CommerceErp.FinanceTests",Op());Directory.CreateDirectory(root);var results=new List<object>();var failed=0;
void Assert(bool x,string message="断言失败"){if(!x)throw new InvalidOperationException(message);}
void Throws<T>(Action action)where T:Exception{try{action();}catch(T){return;}throw new InvalidOperationException("预期异常 "+typeof(T).Name);}
async Task Test(string name,Func<Task> action){try{await action();results.Add(new{name,passed=true});Console.WriteLine("PASS "+name);}catch(Exception e){failed++;results.Add(new{name,passed=false,error=e.ToString()});Console.WriteLine("FAIL "+name+" / "+e);}}
Task Sync(Action action){action();return Task.CompletedTask;}
(CommerceDatabase Db,OrderService S) Fresh(){var db=new CommerceDatabase(Path.Combine(root,Op()));return(db,new(db));}
SampleOrder One(decimal quantity=2)=>SeedData.BaseOrder(Platform.Douyin)with{ExternalOrderId=Op(),ShippingFee=0,OrderDiscount=0,PaidAmount=10,Lines=[SeedData.BaseOrder(Platform.Douyin).Lines[0]with{Quantity=quantity,GoodsAmount=10,MerchantDiscount=0}]};
string Order(OrderService s,SampleOrder? source=null){var o=s.Import(new(Op(),source??One()));s.SaveShippingProfile(o.OrderId,new(Op(),o.Version,new("模拟用户","00000000000","省","市","区","测试路"),true,"测试员","地址"));return o.OrderId;}
WarehouseActionRequest Wh(OrderService s,string id)=>new(Op(),s.ShipmentDetails(id).Summary.Version,"测试员","测试发货");
string Ship(OrderService s,string order,decimal quantity=0)
{
    var p=s.PrepareShipping(order);var lines=p.Lines.Select(l=>new AllocationLine(order,l.ExternalLineId,quantity>0?quantity:l.Remaining,p.Order.Version)).ToList();
    var id=s.CreateShipment(new(Op(),lines,"测试员","配单")).Id;s.StartPicking(id,Wh(s,id));foreach(var c in s.ShipmentDetails(id).Checks)s.ScanShipment(id,new(Op(),s.ShipmentDetails(id).Summary.Version,c.SkuCode,c.Required,"测试员"));
    s.PackShipment(id,Wh(s,id));s.SaveWaybill(id,new(Op(),s.ShipmentDetails(id).Summary.Version,"TEST",Op(),"测试员","测试运单"));s.DispatchShipment(id,Wh(s,id));return id;
}
void Cost(OrderService s,decimal amount=3,string sku="sku-shirt"){var c=s.SkuCosts().Single(x=>x.SkuId==sku);s.SetSkuCost(sku,new(Op(),c.Version,amount,"合成成本依据","测试员","成本设置"));}
void Fees(OrderService s,string order,decimal fee=0,decimal freight=0){var d=s.FinanceOrderDetails(order);s.SetFeePlan(order,new(Op(),d.FeePlan.Version,fee,freight,"合成费用依据","测试员","费用核对"));}
FinanceResult Import(OrderService s,BillInput input)=>s.ImportBill(new(Op(),input,"测试员","合成账单导入"));
FinanceOrder Fin(OrderService s,string id)=>s.FinanceOrderDetails(id).Order;
string External(OrderService s,string id)=>s.Details(id).Summary.ExternalOrderId;
FinanceAction Act(long version=1)=>new(Op(),version,"测试员","测试操作");
string Ready(OrderService s,SampleOrder? source=null){Cost(s);var o=Order(s,source);Ship(s,o);Fees(s,o);Import(s,Input(Csv(Line(External(s,o)))));return o;}
string After(OrderService s,string order,AfterSaleKind kind=AfterSaleKind.ReturnRefund,decimal amount=5,decimal qty=1)
{var p=s.PrepareAfterSale(order);var id=s.CreateAfterSale(new(Op(),order,p.Order.Version,Op(),kind,amount,p.Lines.Select(l=>new AfterSaleSelection(l.ShipmentLineId,qty)).ToList(),"测试员","售后测试")).Id;s.ApproveAfterSale(id,new(Op(),1,"测试员","批准"));return id;}
void Return(OrderService s,string id,decimal good,decimal bad=0){var d=s.AfterSaleDetails(id);var c=d.Components.First();s.InspectReturn(id,new(Op(),d.Summary.Version,Op(),"TEST",[new(c.CaseLineId,c.SkuId,good,bad)],"测试员","实收质检"));}
void Close(OrderService s,string id){var d=s.AfterSaleDetails(id);s.RecordRefund(id,new(Op(),d.Summary.Version,Op(),RefundTrackingState.EvidenceRecorded,d.Summary.RefundAmount,Op(),"模拟人工凭证",DateTimeOffset.UtcNow,true,"测试员","登记"));s.CloseAfterSale(id,new(Op(),s.AfterSaleDetails(id).Summary.Version,"测试员","结案"));}
void Healthy(OrderService s){var a=s.AuditFinance();Assert(a.Passed,string.Join("；",a.Problems));Assert(s.CheckConsistency().Passed);}

await Test("Stage4 v3迁移保留售后库存，历史成本缺失不伪造",()=>Sync(()=>
{var dir=Path.Combine(root,Op());Directory.CreateDirectory(dir);File.Copy(Path.Combine(AppContext.BaseDirectory,"Fixtures/stage4-v3.db"),Path.Combine(dir,"commerce.db"));var db=new CommerceDatabase(dir);var s=new OrderService(db);Assert(db.Read(d=>d.Scalar<int>("SELECT MAX(Version) FROM SchemaMigrations")) == 10);Assert(s.AfterSales().Total==2&&s.Quarantine().Any(q=>q.Quantity>0));Assert(s.ShipmentCosts().Count>0&&s.ShipmentCosts().All(c=>c.Cost is null));Healthy(s);}));
await Test("预览无副作用，导入保存原文和来源未核验标识",()=>Sync(()=>
{var(_,s)=Fresh();var input=Input(Csv(Line("00000000000000000001")));var p=s.PreviewBill(input);Assert(p.Valid&&p.NewRows==1&&s.BillBatches().Count==0);var result=Import(s,input);Assert(s.OriginalBill(result.Id)==input.Content&&s.BillRows().Items[0].ExternalOrderId=="00000000000000000001");Healthy(s);}));
await Test("自定义列名类型映射及带逗号引号的订单号",()=>Sync(()=>
{var(_,s)=Fresh();var input=Input("交易,订单,类别,元,时间,币种,售后\r\nT1,\"A,\"\"B\"\"\",SALE,10,2026-10-01T00:00:00Z,CNY,\r\n")with{Columns=new("交易","订单","类别","元","时间","币种","售后"),Types=new(){{"SALE",BillKind.Sale}}};Import(s,input);Assert(s.BillRows().Items[0].ExternalOrderId=="A,\"B\"");}));
await Test("非法CSV、未知类型、金额精度、币种和缺少时区拒绝",()=>Sync(()=>
{var(_,s)=Fresh();foreach(var row in new[]{Line("O").Replace(",收入,",",未知,"),Line("O").Replace(",10,",",-10,"),Line("O").Replace(",10,",",0,"),Line("O").Replace(",10,",",1.001,"),Line("O").Replace(",CNY,",",USD,"),Line("O",time:"2026-10-01T00:00:00"),Line("O","退款"),"\"unfinished"})Assert(!s.PreviewBill(Input(Csv(row))).Valid,row);Assert(!s.PreviewBill(Input(Csv(Line("O")))with{Columns=new("订单号")}).Valid);Assert(s.BillBatches().Count==0);}));
await Test("500行及120KB限制，业务标识保持文本",()=>Sync(()=>
{var(_,s)=Fresh();Assert(!s.PreviewBill(Input(Csv(Enumerable.Range(0,501).Select(i=>Line("X",tx:"T"+i)).ToArray()))).Valid);Assert(!s.PreviewBill(Input(new string('x',120001))).Valid);Assert(!s.PreviewBill(Input(Csv(Line("X")))with{Platform=(Platform)99}).Valid);}));
await Test("操作键、批次号、跨文件流水防重，冲突整批回滚",()=>Sync(()=>
{var(_,s)=Fresh();var input=Input(Csv(Line("O",tx:"TX")));var r=new ImportBillRequest(Op(),input,"测试员","导入");s.ImportBill(r);Assert(s.ImportBill(r).Replayed&&Import(s,input).Replayed);var duplicate=Import(s,input with{BatchNumber=Op()});Assert(s.BillBatches().Single(b=>b.Id==duplicate.Id).NewRows==0);Throws<ConflictException>(()=>Import(s,input with{Content=Csv(Line("O",amount:11,tx:"TX"))}));Throws<BusinessRuleException>(()=>Import(s,Input(Csv(Line("NEW"),Line("O",amount:11,tx:"TX")))));Assert(s.BillRows().Total==1);Healthy(s);}));
await Test("同文件重复行跳过，不同内容重复行拒绝",()=>Sync(()=>
{var(_,s)=Fresh();var line=Line("O",tx:"T");var p=s.PreviewBill(Input(Csv(line,line)));Assert(p.NewRows==1&&p.DuplicateRows==1);Import(s,Input(Csv(line,line)));Assert(s.BillRows().Total==1);Assert(!s.PreviewBill(Input(Csv(Line("P",tx:"N"),Line("P",amount:12,tx:"N")))).Valid);}));
await Test("十个并发导入同流水只落一笔",async()=>
{var(_,s)=Fresh();var csv=Csv(Line("O",tx:"RACE"));await Task.WhenAll(Enumerable.Range(0,10).Select(_=>Task.Run(()=>Import(s,Input(csv)))));Assert(s.BillRows().Total==1&&s.BillBatches().Sum(b=>b.NewRows)==1);Healthy(s);});
await Test("异常及进程中断整批回滚，重启可再次导入",()=>Sync(()=>
{var(db,s)=Fresh();var bad=new OrderService(db,p=>{if(p=="finance-after-row")throw new IOException("故障");});Throws<IOException>(()=>Import(bad,Input(Csv(Line("O"),Line("P")))));Assert(s.BillRows().Total==0&&s.BillBatches().Count==0);var info=new ProcessStartInfo("dotnet"){UseShellExecute=false,CreateNoWindow=true};info.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);info.ArgumentList.Add("--crash-import");info.ArgumentList.Add(Path.GetDirectoryName(db.DatabasePath)!);using var process=Process.Start(info)!;Assert(process.WaitForExit(15000)&&process.ExitCode==75);var reopened=new OrderService(new CommerceDatabase(Path.GetDirectoryName(db.DatabasePath)!));Assert(reopened.BillRows().Total==0&&reopened.BillBatches().Count==0);Import(reopened,Input(Csv(Line("O"))));Healthy(reopened);}));
await Test("先账单后订单可以重新匹配，未匹配退款保留差异",()=>Sync(()=>
{var(_,s)=Fresh();var source=One();Import(s,Input(Csv(Line(source.ExternalOrderId),Line(source.ExternalOrderId,"退款",5,sourceCase:"MISSING"))));Assert(s.BillRows(unmatched:true).Total==2);var id=Order(s,source);s.RematchBills(Act());Assert(s.BillRows(unmatched:true).Total==1&&Fin(s,id).Problems.Any(p=>p.Contains("售后关联")));}));
await Test("人工匹配留痕且禁止跨店铺，旧版本写入拒绝",()=>Sync(()=>
{var(_,s)=Fresh();var id=Order(s);Import(s,Input(Csv(Line("wrong"))));var row=s.BillRows().Items[0];var request=new MatchBillRequest(Op(),row.Version,id,null,"原单核实依据","测试员","更正关联");s.MatchBill(row.Id,request);Assert(s.BillDetails(row.Id).History.Count==1&&s.BillDetails(row.Id).Row.ExternalOrderId=="wrong");Throws<ConflictException>(()=>s.MatchBill(row.Id,request with{OperationId=Op()}));var other=Order(s,SeedData.BaseOrder(Platform.Jd)with{ExternalOrderId=Op(),PaidAmount=111});Throws<BusinessRuleException>(()=>s.MatchBill(row.Id,request with{OperationId=Op(),ExpectedVersion=2,OrderId=other}));s.RematchBills(Act());Assert(s.BillDetails(row.Id).Row.OrderId==id);}));
await Test("跨日多笔收入合计匹配，不要求一单一流水",()=>Sync(()=>
{var(_,s)=Fresh();Cost(s);var id=Order(s);Ship(s,id);Fees(s,id);var ext=External(s,id);Import(s,Input(Csv(Line(ext,amount:4),Line(ext,amount:6,time:"2026-10-02T12:00:00+08:00"))));Assert(Fin(s,id).Profit==4&&Fin(s,id).RowCount==2);}));
await Test("缺失成本和未确认零费用都不能显示完整利润",()=>Sync(()=>
{var(_,s)=Fresh();var id=Order(s);Ship(s,id);Import(s,Input(Csv(Line(External(s,id)))));Assert(Fin(s,id).Profit is null&&Fin(s,id).NetCost is null);Fees(s,id);Assert(Fin(s,id).Profit is null);var c=s.ShipmentCosts(id).Single();s.FillShipmentCost(c.ShipmentLineId,c.SkuId,new(Op(),c.Version,3,"历史依据","测试员","补录"));Assert(Fin(s,id).Profit==4);Throws<ConflictException>(()=>s.FillShipmentCost(c.ShipmentLineId,c.SkuId,new(Op(),2,4,"修改","测试员","修改")));Healthy(s);}));
await Test("单位成本修改不追改出库，重复发货无重复成本",()=>Sync(()=>
{var(_,s)=Fresh();Cost(s,3);var id=Order(s);var ship=Ship(s,id);Cost(s,9);s.DispatchShipment(ship,Wh(s,ship));Assert(s.ShipmentCosts(id).Single().Cost==6&&s.ShipmentCosts(id).Count==1);var next=Order(s);Ship(s,next);Assert(s.ShipmentCosts(next).Single().Cost==18);}));
await Test("六位小数单位成本和分位舍入，负成本非法",()=>Sync(()=>
{var(_,s)=Fresh();Cost(s,0.333333m);var id=Order(s,One(0.125m));Ship(s,id);Assert(s.ShipmentCosts(id).Single().Cost==0.04m);Throws<BusinessRuleException>(()=>Cost(s,-1));Throws<BusinessRuleException>(()=>Cost(s,0.0000001m));}));
await Test("部分发货不展示完整利润，后续出库补齐成本",()=>Sync(()=>
{var(_,s)=Fresh();Cost(s);var id=Order(s);Ship(s,id,1);Fees(s,id);Import(s,Input(Csv(Line(External(s,id)))));Assert(Fin(s,id).Profit is null);Ship(s,id,1);Assert(Fin(s,id).Profit==4);}));
await Test("费用差异与退款未达均阻止完整利润及导出",()=>Sync(()=>
{var(_,s)=Fresh();var id=Ready(s);Fees(s,id,1,2);Assert(Fin(s,id).Problems.Count==2);Throws<ConflictException>(()=>s.CreateFinanceExport(new(Op(),Op(),"测试财务",[id],"测试员","导出")));Import(s,Input(Csv(Line(External(s,id),"平台费",1),Line(External(s,id),"运费",2))));Assert(Fin(s,id).Profit==1);var c=After(s,id,AfterSaleKind.RefundOnly);Assert(Fin(s,id).Profit is null);Close(s,c);Assert(Fin(s,id).Refund==0&&Fin(s,id).Profit is null);}));
await Test("人工退款凭证不伪造结算，账单退款也不改平台状态",()=>Sync(()=>
{var(_,s)=Fresh();var id=Ready(s);var c=After(s,id,AfterSaleKind.RefundOnly);var d=s.AfterSaleDetails(c);Import(s,Input(Csv(Line(External(s,id),"退款",5,sourceCase:d.Summary.SourceCaseNumber))));Assert(s.AfterSaleDetails(c).Summary.RefundState==RefundTrackingState.Pending&&Fin(s,id).Refund==5&&Fin(s,id).Profit==-1);Throws<ConflictException>(()=>s.WithdrawAfterSale(c,new(Op(),d.Summary.Version,"测试员","不能释放")));}));
await Test("退款多笔累计及超退款显示差异，不能靠收入抵消",()=>Sync(()=>
{var(_,s)=Fresh();var id=Ready(s);var c=After(s,id,AfterSaleKind.RefundOnly);var source=s.AfterSaleDetails(c).Summary.SourceCaseNumber;Import(s,Input(Csv(Line(External(s,id),"退款",2,sourceCase:source),Line(External(s,id),"退款",3,sourceCase:source))));Assert(Fin(s,id).Profit==-1);Import(s,Input(Csv(Line(External(s,id),"退款",1,sourceCase:source),Line(External(s,id),amount:1))));Assert(Fin(s,id).Profit is null&&Fin(s,id).Problems.Count>=2);}));
await Test("良品按原快照冲回，残次品保留成本，未结案不完成",()=>Sync(()=>
{var(_,s)=Fresh();var id=Ready(s);var c=After(s,id,amount:10,qty:2);Return(s,c,1,1);Import(s,Input(Csv(Line(External(s,id),"退款",10,sourceCase:s.AfterSaleDetails(c).Summary.SourceCaseNumber))));Assert(Fin(s,id).NetCost==3&&Fin(s,id).Profit is null);Close(s,c);Assert(Fin(s,id).Profit==-3&&s.Quarantine().Single(q=>q.SkuId=="sku-shirt").Quantity==1);Cost(s,20);Assert(Fin(s,id).NetCost==3);Healthy(s);}));
await Test("分批良品冲回按累计分位计算，不超过原成本",()=>Sync(()=>
{var(_,s)=Fresh();Cost(s,0.333333m);var id=Order(s,One(3));Ship(s,id);var c=After(s,id,amount:10,qty:3);Return(s,c,1);Assert(s.ShipmentCosts(id).Single().ReturnCredit==0.33m);Return(s,c,1);Assert(s.ShipmentCosts(id).Single().ReturnCredit==0.67m);Return(s,c,1);Assert(s.ShipmentCosts(id).Single().ReturnCredit==1m);Healthy(s);}));
await Test("账单作废保留原文并退出汇总，已作废流水不可复用",()=>Sync(()=>
{var(_,s)=Fresh();var id=Ready(s);var batch=s.BillBatches().Single();var raw=s.OriginalBill(batch.Id);s.VoidBill(batch.Id,Act());Assert(Fin(s,id).Income==0&&s.OriginalBill(batch.Id)==raw&&s.BillRows().Items[0].State=="Voided");Assert(!s.PreviewBill(Input(raw)).Valid);Healthy(s);}));
await Test("组合商品成本按组件冻结，商品汇总分位守恒",()=>Sync(()=>
{var(_,s)=Fresh();foreach(var sku in s.SkuCosts())Cost(s,1,sku.SkuId);var sample=SeedData.Samples().First(x=>x.Order.Lines.Any(l=>l.PlatformSkuId.Contains("kit",StringComparison.OrdinalIgnoreCase))||x.Descriptor.Id.Contains("bundle",StringComparison.OrdinalIgnoreCase)).Order with{ExternalOrderId=Op()};var id=Order(s,sample);Ship(s,id);Fees(s,id);Import(s,Input(Csv(Line(External(s,id),amount:sample.PaidAmount)),platform:sample.Platform));var report=s.FinanceReport();Assert(report.Products.Sum(p=>p.Income)==sample.PaidAmount&&report.Products.Sum(p=>p.Profit)==Fin(s,id).Profit);Healthy(s);}));
await Test("导出快照防重、哈希可验证、重启内容不变、CSV防公式",()=>Sync(()=>
{var(db,s)=Fresh();var id=Ready(s,One()with{ExternalOrderId="=1+2"});var r=new CreateFinanceExportRequest(Op(),Op(),"本地财务交接",[id],"测试员","导出");var e=s.CreateFinanceExport(r);var doc=s.FinanceExportDocument(e.Id);Assert(doc.Csv.Contains("'\u003d1+2")&&doc.Summary.Hash==Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(doc.Json))).ToLowerInvariant());Assert(s.CreateFinanceExport(r).Replayed&&s.CreateFinanceExport(r with{OperationId=Op()}).Replayed);Throws<ConflictException>(()=>s.CreateFinanceExport(r with{OperationId=Op(),ExportNumber=Op()}));s.MarkFinanceExported(e.Id,Act());var restarted=new OrderService(new CommerceDatabase(Path.GetDirectoryName(db.DatabasePath)!));Assert(restarted.FinanceExportDocument(e.Id).Json==doc.Json&&restarted.FinanceExports().Single().State=="Exported");Healthy(s);}));
await Test("导出后禁止改关联或作废批次，后续业务变化标记过期",()=>Sync(()=>
{var(_,s)=Fresh();var id=Ready(s);var e=s.CreateFinanceExport(new(Op(),Op(),"财务",[id],"测试员","导出"));Throws<ConflictException>(()=>s.VoidBill(s.BillBatches().Single().Id,Act()));var row=s.BillRows().Items[0];Throws<ConflictException>(()=>s.MatchBill(row.Id,new(Op(),1,id,null,"依据","测试员","改动")));After(s,id,AfterSaleKind.RefundOnly);Assert(s.FinanceExportDocument(e.Id).Stale);Throws<ConflictException>(()=>s.MarkFinanceExported(e.Id,Act()));Healthy(s);}));
await Test("导出故障回滚，不留半个快照或占用订单",()=>Sync(()=>
{var(db,s)=Fresh();var id=Ready(s);var faulty=new OrderService(db,p=>{if(p=="finance-after-export")throw new IOException();});var request=new CreateFinanceExportRequest(Op(),Op(),"财务",[id],"测试员","导出");Throws<IOException>(()=>faulty.CreateFinanceExport(request));Assert(s.FinanceExports().Count==0);s.CreateFinanceExport(request);Healthy(s);}));
await Test("费用和成本版本冲突、分页参数以及无效请求拒绝",()=>Sync(()=>
{var(_,s)=Fresh();var id=Ready(s);Throws<ConflictException>(()=>s.SetFeePlan(id,new(Op(),0,0,0,"依据","测试员","旧版本")));Throws<BusinessRuleException>(()=>s.FinanceOrders(page:0));Throws<BusinessRuleException>(()=>s.BillRows(pageSize:101));Throws<BusinessRuleException>(()=>s.SetSkuCost("unknown",new(Op(),0,1,"依据","测试员","错误SKU")));Throws<BusinessRuleException>(()=>s.CreateFinanceExport(new(Op(),Op(),"财务",[],"测试员","空导出")));Assert(!s.PreviewBill(null!).Valid);}));
await Test("审计发现成本快照损坏，不自动修账",()=>Sync(()=>
{var(db,s)=Fresh();Ready(s);db.Write(d=>d.Execute("UPDATE ShipmentCosts SET CostCents=CostCents+1"));Assert(!s.AuditFinance().Passed&&!s.CheckConsistency().Passed);}));
await Test("未出库整单取消按全额账单退款对平，成本为明确零",()=>Sync(()=>
{var(_,s)=Fresh();var id=Order(s);var p=s.PrepareAfterSale(id);var c=s.CreateAfterSale(new(Op(),id,p.Order.Version,"CANCEL-FIN",AfterSaleKind.CancelUnshipped,10,[],"测试员","取消")).Id;s.ApproveAfterSale(c,new(Op(),1,"测试员","批准"));Fees(s,id);Import(s,Input(Csv(Line(External(s,id)),Line(External(s,id),"退款",10,sourceCase:"CANCEL-FIN"))));Assert(Fin(s,id).Profit==0&&Fin(s,id).NetCost==0&&s.ShipmentCosts(id).Count==0);Healthy(s);}));
await Test("审计检出账单金额或导出CSV被改，原文摘要不能掩盖损坏",()=>Sync(()=>
{var(db,s)=Fresh();var id=Ready(s);s.CreateFinanceExport(new(Op(),Op(),"财务",[id],"测试员","导出"));db.Write(d=>d.Execute("UPDATE BillRows SET AmountCents=AmountCents+1"));Assert(s.AuditFinance().Problems.Any(p=>p.Contains("明细与原文")));db.Write(d=>d.Execute("UPDATE FinanceExports SET CsvContent='changed'"));Assert(s.AuditFinance().Problems.Any(p=>p.Contains("CSV哈希")));}));
Console.WriteLine($"TOTAL {results.Count} / FAILED {failed}");if(args.Length>0){Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[0]))!);File.WriteAllText(args[0],Json(new{passed=failed==0,total=results.Count,failed,results}));}return failed==0?0:1;
