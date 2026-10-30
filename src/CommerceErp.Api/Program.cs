using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CommerceErp.Core;
using CommerceErp.Infrastructure;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

try
{
var settings = CommerceErp.Api.ServerSettings.Load(args);
var sessionToken = settings.ReadToken();
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
if (settings.Standalone) builder.Services.AddWindowsService(options => options.ServiceName = "CommerceErp");
builder.Logging.ClearProviders();
builder.WebHost.ConfigureKestrel(k =>
{
    k.Listen(IPAddress.Parse(settings.ListenAddress), settings.Port, listen =>
    {
        if (settings.CertificateFile is not null) listen.UseHttps(settings.Resolve(settings.CertificateFile), Environment.GetEnvironmentVariable("ERP_HTTPS_PASSWORD"));
    });
    k.Limits.MaxRequestBodySize = 262144;
});
FileStream? dataLease = null;
if (settings.DatabaseProvider == "Sqlite")
{
    var directory = settings.Resolve(settings.DataDirectory); Directory.CreateDirectory(directory);
    dataLease = new FileStream(Path.Combine(directory,"service.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
}
using var serviceDataLease = dataLease;
var database = settings.OpenDatabase();
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
    o.SerializerOptions.MaxDepth = 32;
    foreach (var converter in ErpJson.Options.Converters) o.SerializerOptions.Converters.Add(converter);
});
builder.Services.AddSingleton(new AccountService(database));
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    options.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions { PermitLimit = 60, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
builder.Services.AddSingleton(new WorkspaceStore(database, settings.Standalone ? "SharedServiceDemo" : "LocalTransactionalDemo"));
builder.Services.AddSingleton(s => new OrderService(s.GetRequiredService<WorkspaceStore>().Database));
builder.Services.AddSingleton(s => new ShipmentDeliveryProcessor(s.GetRequiredService<WorkspaceStore>().Database, s.GetServices<IShipmentGateway>()));
builder.Services.AddHostedService<CommerceErp.Api.DeliveryWorker>();
builder.Services.AddSingleton(s=>new WorkerHealthService(s.GetRequiredService<WorkspaceStore>().Database));
builder.Services.AddSingleton(s=>new InventoryDocumentService(s.GetRequiredService<WorkspaceStore>().Database));
builder.Services.AddSingleton(s=>new MasterDataService(s.GetRequiredService<WorkspaceStore>().Database));
builder.Services.AddSingleton(s=>new WorkTaskService(s.GetRequiredService<WorkspaceStore>().Database));
builder.Services.AddSingleton(s=>new OutboxProcessor(s.GetRequiredService<WorkspaceStore>().Database,s.GetServices<IOutboxHandler>()));
builder.Services.AddHostedService<CommerceErp.Api.OutboxWorker>();
var app = builder.Build();
app.UseRouting();
app.UseRateLimiter();
var accounts = app.Services.GetRequiredService<AccountService>();
System.Diagnostics.Process? parentProcess = null;
if (!settings.Standalone && int.TryParse(Environment.GetEnvironmentVariable("ERP_PARENT_PROCESS_ID"), out var parentId))
{
    try
    {
        parentProcess = System.Diagnostics.Process.GetProcessById(parentId);
        parentProcess.Exited += (_, _) => app.Lifetime.StopApplication();
        parentProcess.EnableRaisingEvents = true;
        if (parentProcess.HasExited) return;
    }
    catch (ArgumentException) { return; }
}
app.Use(async (context, next) =>
{
    var supplied = context.Request.Headers["X-Erp-Session"].ToString();
    if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(supplied)),
        SHA256.HashData(Encoding.UTF8.GetBytes(sessionToken))))
    {
        context.Response.StatusCode = 401;
        await context.Response.WriteAsJsonAsync(new { message = "缺少有效的服务访问凭证。" });
        return;
    }
    context.Response.Headers.CacheControl = "no-store";
    try
    {
        var expectedIdentity = context.Request.Headers["X-Erp-Database"].ToString();
        if (expectedIdentity.Length > 0 && !string.Equals(expectedIdentity, database.InstanceId, StringComparison.Ordinal))
        { context.Response.StatusCode = 412; await context.Response.WriteAsJsonAsync(new { message = "数据库身份变化，已阻止本次操作。请核对连接配置，保留待重试记录。" }); return; }
        await CommerceErp.Api.AccountEndpoints.Invoke(context, () => next(context), database, accounts);
    }
    catch (AuthenticationException e) { context.Response.StatusCode = 401; await context.Response.WriteAsJsonAsync(new { message = e.Message }); }
    catch (AccessDeniedException e) { context.Response.StatusCode = 403; await context.Response.WriteAsJsonAsync(new { message = e.Message }); }
    catch (ConflictException e) { context.Response.StatusCode = 409; await context.Response.WriteAsJsonAsync(new { message = e.Message }); }
    catch (DatabaseBusyException e) { context.Response.StatusCode = 503; await context.Response.WriteAsJsonAsync(new { message = e.Message }); }
    catch (BusinessRuleException e) { context.Response.StatusCode = 400; await context.Response.WriteAsJsonAsync(new { message = e.Message }); }
    catch (JsonException) { context.Response.StatusCode = 400; await context.Response.WriteAsJsonAsync(new { message = "JSON 格式、字段或类型不符合样例契约。" }); }
    catch (BadHttpRequestException e) { context.Response.StatusCode = e.StatusCode; await context.Response.WriteAsJsonAsync(new { message = "请求格式错误或内容超过 256 KB。" }); }
    catch (Exception e) when (e is not OperationCanceledException)
    {
        Console.Error.WriteLine($"{DateTimeOffset.UtcNow:O} request failed: {e.GetType().Name}");
        context.Response.StatusCode = 500;
        await context.Response.WriteAsJsonAsync(new { message = "服务处理失败，请检查服务状态和数据库连接。原有数据不会被自动重置。" });
    }
});
CommerceErp.Api.AccountEndpoints.Map(app);
app.MapGet("/api/health", () => new ServiceHealth("ok", database.Read(CommerceDatabase.BusinessMode)?"BusinessWorkspace":settings.Standalone ? "SharedServiceDemo" : "LocalTransactionalDemo", settings.DatabaseProvider, database.InstanceId, CommerceDatabase.SchemaVersion, 7, false, settings.Standalone));
app.MapGet("/api/tasks",(WorkTaskService s,int page=1,int pageSize=20,string search="",string state="",string kind="")=>s.List(page,pageSize,search,state,kind));
app.MapGet("/api/tasks/summary",(WorkTaskService s)=>s.Summary());
app.MapGet("/api/tasks/{kind}/{id}",(WorkTaskKind kind,string id,WorkTaskService s)=>s.Details(kind,id));
app.MapPost("/api/tasks/{kind}/{id}/retry",(WorkTaskKind kind,string id,RetryWorkTaskRequest r,WorkTaskService s)=>s.Retry(kind,id,r));
CommerceErp.Api.MasterEndpoints.Map(app);
CommerceErp.Api.InventoryDocumentEndpoints.Map(app);
app.MapGet("/api/tasks/health",(WorkerHealthService s)=>s.Read());
app.MapGet("/api/bootstrap", (WorkspaceStore store) => store.ReadAsync());
app.MapPut("/api/readiness/{platform}", (Platform platform, ReadinessUpdate update, WorkspaceStore store) => store.SaveReadinessAsync(platform, update));
app.MapPut("/api/mappings", (MappingUpdate update, WorkspaceStore store) => store.SaveMappingAsync(update));
app.MapGet("/api/samples/{id}", (string id) =>
{
    var sample = SeedData.Samples().SingleOrDefault(x => x.Descriptor.Id == id);
    return sample is null ? Results.NotFound(new { message = "样例不存在。" }) : Results.Json(sample.Order, ErpJson.Options);
});
app.MapPost("/api/validate", (SampleOrder order, WorkspaceStore store) => store.ValidateAsync(order));
app.MapGet("/api/reports", (WorkspaceStore store) => store.ReportsAsync());
app.MapGet("/api/business/dashboard", (OrderService service) => service.Dashboard());
app.MapGet("/api/business/consistency", (OrderService service) => service.CheckConsistency());
app.MapGet("/api/orders", (OrderService service, int page = 1, int pageSize = 20, string? search = null, ReviewState? state = null) => service.List(page, pageSize, search, state));
app.MapPost("/api/orders", (ImportOrderRequest request, OrderService service) => service.Import(request));
app.MapGet("/api/orders/{id}", (string id, OrderService service) => service.Details(id));
app.MapPost("/api/orders/{id}/review", (string id, OrderActionRequest request, OrderService service) => service.Review(id, request));
app.MapPost("/api/orders/{id}/cancel", (string id, OrderActionRequest request, OrderService service) => service.Cancel(id, request));
app.MapPost("/api/orders/{id}/correct", (string id, CorrectOrderRequest request, OrderService service) => service.Correct(id, request));
app.MapGet("/api/inventory", (OrderService service) => service.Inventory());
app.MapGet("/api/inventory/ledger", (OrderService service, string? sku = null) => service.StockLedger(sku));
app.MapPost("/api/inventory/{sku}/receive", (string sku, ReceiveStockRequest request, OrderService service) => service.Receive(sku, request));
app.MapPost("/api/inventory/{sku}/policy", (string sku, StockPolicyRequest request, OrderService service) => service.Policy(sku, request));
app.MapGet("/api/warehouse/orders", (OrderService s, string? search = null) => s.ShippingCandidates(search));
app.MapGet("/api/warehouse/orders/{id}", (string id, OrderService s) => s.PrepareShipping(id));
app.MapPost("/api/warehouse/orders/{id}/address", (string id, SaveShippingProfileRequest r, OrderService s) => s.SaveShippingProfile(id, r));
app.MapGet("/api/warehouse/shipments", (OrderService s, int page = 1, int pageSize = 20, string? search = null, ShipmentState? state = null) => s.Shipments(page, pageSize, search, state));
app.MapPost("/api/warehouse/shipments", (CreateShipmentRequest r, OrderService s) => s.CreateShipment(r));
app.MapGet("/api/warehouse/shipments/{id}", (string id, OrderService s) => s.ShipmentDetails(id));
app.MapPost("/api/warehouse/shipments/{id}/split", (string id, SplitShipmentRequest r, OrderService s) => s.SplitShipment(id, r));
app.MapPost("/api/warehouse/merge", (MergeShipmentsRequest r, OrderService s) => s.MergeShipments(r));
app.MapPost("/api/warehouse/shipments/{id}/pick", (string id, WarehouseActionRequest r, OrderService s) => s.StartPicking(id, r));
app.MapPost("/api/warehouse/shipments/{id}/scan", (string id, ScanShipmentRequest r, OrderService s) => s.ScanShipment(id, r));
app.MapPost("/api/warehouse/shipments/{id}/reset", (string id, WarehouseActionRequest r, OrderService s) => s.ResetVerification(id, r));
app.MapPost("/api/warehouse/shipments/{id}/pack", (string id, WarehouseActionRequest r, OrderService s) => s.PackShipment(id, r));
app.MapPost("/api/warehouse/shipments/{id}/waybill", (string id, WaybillRequest r, OrderService s) => s.SaveWaybill(id, r));
app.MapPost("/api/warehouse/shipments/{id}/void", (string id, WarehouseActionRequest r, OrderService s) => s.VoidShipment(id, r));
app.MapPost("/api/warehouse/shipments/{id}/dispatch", (string id, WarehouseActionRequest r, OrderService s) => s.DispatchShipment(id, r));
app.MapGet("/api/warehouse/deliveries", (OrderService s) => s.Deliveries());
app.MapPost("/api/warehouse/deliveries/{id}/retry", (string id, WarehouseActionRequest r, OrderService s) => s.RetryDelivery(id, r));
app.MapGet("/api/warehouse/barcodes", (OrderService s) => s.Barcodes());
app.MapPost("/api/warehouse/barcodes", (SaveBarcodeRequest r, OrderService s) => s.SaveBarcode(r));
app.MapGet("/api/warehouse/audit", (OrderService s) => s.AuditWarehouse());
if (!settings.Standalone) app.MapPost("/api/shutdown", (IHostApplicationLifetime lifetime) =>
{
    lifetime.StopApplication();
    return Results.Ok();
});
app.MapGet("/api/after-sales/orders", (OrderService s, string? search = null) => s.AfterSaleCandidates(search));
app.MapGet("/api/after-sales/orders/{id}", (string id, OrderService s) => s.PrepareAfterSale(id));
app.MapGet("/api/after-sales/cases", (OrderService s, int page = 1, int pageSize = 20, string? search = null, AfterSaleState? state = null) => s.AfterSales(page, pageSize, search, state));
app.MapPost("/api/after-sales/cases", (CreateAfterSaleRequest r, OrderService s) => s.CreateAfterSale(r));
app.MapGet("/api/after-sales/cases/{id}", (string id, OrderService s) => s.AfterSaleDetails(id));
app.MapPost("/api/after-sales/cases/{id}/approve", (string id, AfterSaleActionRequest r, OrderService s) => s.ApproveAfterSale(id, r));
app.MapPost("/api/after-sales/cases/{id}/reject", (string id, AfterSaleActionRequest r, OrderService s) => s.RejectAfterSale(id, r));
app.MapPost("/api/after-sales/cases/{id}/withdraw", (string id, AfterSaleActionRequest r, OrderService s) => s.WithdrawAfterSale(id, r));
app.MapPost("/api/after-sales/cases/{id}/close", (string id, AfterSaleActionRequest r, OrderService s) => s.CloseAfterSale(id, r));
app.MapPost("/api/after-sales/cases/{id}/inspect", (string id, InspectReturnRequest r, OrderService s) => s.InspectReturn(id, r));
app.MapPost("/api/after-sales/cases/{id}/refund", (string id, RecordRefundRequest r, OrderService s) => s.RecordRefund(id, r));
app.MapGet("/api/after-sales/quarantine", (OrderService s) => s.Quarantine());
app.MapGet("/api/after-sales/quarantine/ledger", (OrderService s) => s.QuarantineLedger());
app.MapGet("/api/after-sales/audit", (OrderService s) => s.AuditAfterSales());
app.MapPost("/api/finance/preview", (BillInput r, OrderService s) => s.PreviewBill(r));
app.MapGet("/api/finance/batches", (OrderService s) => s.BillBatches());
app.MapPost("/api/finance/batches", (ImportBillRequest r, OrderService s) => s.ImportBill(r));
app.MapGet("/api/finance/batches/{id}/original", (string id, OrderService s) => new { content = s.OriginalBill(id) });
app.MapPost("/api/finance/batches/{id}/void", (string id, FinanceAction r, OrderService s) => s.VoidBill(id,r));
app.MapGet("/api/finance/rows", (OrderService s,int page=1,int pageSize=20,string? search=null,bool unmatched=false) => s.BillRows(page,pageSize,search,unmatched));
app.MapGet("/api/finance/rows/{id}", (string id,OrderService s) => s.BillDetails(id));
app.MapPost("/api/finance/rows/{id}/match", (string id,MatchBillRequest r,OrderService s) => s.MatchBill(id,r));
app.MapPost("/api/finance/rematch", (FinanceAction r,OrderService s) => s.RematchBills(r));
app.MapGet("/api/finance/orders", (OrderService s,int page=1,int pageSize=20,string? search=null,bool differences=false) => s.FinanceOrders(page,pageSize,search,differences));
app.MapGet("/api/finance/orders/{id}", (string id,OrderService s) => s.FinanceOrderDetails(id));
app.MapPost("/api/finance/orders/{id}/fees", (string id,SetFeePlanRequest r,OrderService s) => s.SetFeePlan(id,r));
app.MapGet("/api/finance/sku-costs", (OrderService s) => s.SkuCosts());
app.MapPost("/api/finance/sku-costs/{id}", (string id,SetSkuCostRequest r,OrderService s) => s.SetSkuCost(id,r));
app.MapGet("/api/finance/costs", (OrderService s,string? order=null) => s.ShipmentCosts(order));
app.MapPost("/api/finance/costs/{line}/{sku}", (string line,string sku,FillCostRequest r,OrderService s) => s.FillShipmentCost(line,sku,r));
app.MapGet("/api/finance/report", (OrderService s) => s.FinanceReport());
app.MapGet("/api/finance/exports", (OrderService s) => s.FinanceExports());
app.MapPost("/api/finance/exports", (CreateFinanceExportRequest r,OrderService s) => s.CreateFinanceExport(r));
app.MapGet("/api/finance/exports/{id}", (string id,OrderService s) => s.FinanceExportDocument(id));
app.MapPost("/api/finance/exports/{id}/saved", (string id,FinanceAction r,OrderService s) => s.MarkFinanceExported(id,r));
app.MapGet("/api/finance/audit", (OrderService s) => s.AuditFinance());
await app.StartAsync();
var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!;
Console.WriteLine("ERP_READY|" + addresses.Addresses.Single());
Console.Out.Flush();
await app.WaitForShutdownAsync();
await app.DisposeAsync();
parentProcess?.Dispose();

}
catch (Exception error)
{
    Console.Error.WriteLine(error is InvalidDataException ? error.Message : "服务启动或运行失败（" + error.GetType().Name + "）。请检查配置、证书、数据库及文件权限。");
    Environment.ExitCode = 1;
}
