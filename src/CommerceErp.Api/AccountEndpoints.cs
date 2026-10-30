using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CommerceErp.Core;
using CommerceErp.Infrastructure;
using Microsoft.AspNetCore.Routing;

namespace CommerceErp.Api;

public static class AccountEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/security/status",(AccountService s)=>s.Status());
        app.MapPost("/api/security/setup",(HttpContext c,SetupAccountRequest r,AccountService s)=>
        {
            if(c.Connection.RemoteIpAddress is not { } ip||!IPAddress.IsLoopback(ip))throw new AccessDeniedException("首次管理员须在服务机器本地建立。");
            return s.Setup(r);
        }).RequireRateLimiting("login");
        app.MapPost("/api/security/login",(LoginRequest r,AccountService s)=>s.Login(r)).RequireRateLimiting("login");
        app.MapGet("/api/security/me",()=>SecurityContext.Current!.User);
        app.MapPost("/api/security/logout",(HttpContext c,AccountService s)=>{s.Logout(Token(c));return Results.Ok(new{message="已退出登录。"});});
        app.MapPost("/api/security/password",(HttpContext c,ChangePasswordRequest r,AccountService s)=>{s.ChangePassword(Token(c),r);return Results.Ok(new{message="密码已更新，请重新登录。"});}).RequireRateLimiting("login");
        app.MapGet("/api/security/accounts",(AccountService s)=>s.Accounts());
        app.MapPost("/api/security/accounts",(SaveAccountRequest r,AccountService s)=>s.Save(null,r));
        app.MapPut("/api/security/accounts/{id}",(string id,SaveAccountRequest r,AccountService s)=>s.Save(id,r));
        app.MapPost("/api/security/accounts/{id}/password",(string id,ResetPasswordRequest r,AccountService s)=>{s.ResetPassword(id,r);return Results.Ok(new{message="密码已重置，旧会话已撤销。"});});
        app.MapGet("/api/security/audit",(AccountService s)=>s.AuditLog());
        app.MapGet("/api/security/audit/query",(AccountService s,int page=1,int pageSize=50,string actor="",string action="",string outcome="",string resource="",string? from=null,string? to=null,long? snapshotId=null)=>s.QueryAudit(new(page,pageSize,actor,action,outcome,resource,from,to,snapshotId)));
        app.MapPost("/api/security/audit/export",(AccountService s,AuditExportRequest r)=>s.ExportAudit(r));
    }
    private static string? String(JsonNode? value) { if(value is null)return null; if(value is JsonValue v && v.TryGetValue<string>(out var text))return text;throw new JsonException(); }
    private static void ValidateKeys(JsonElement value)
    {
        if(value.ValueKind==JsonValueKind.Object)
        {
            var keys=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(var field in value.EnumerateObject())
            {if(!keys.Add(field.Name))throw new JsonException("字段不能重复。");ValidateKeys(field.Value);}
        }
        else if(value.ValueKind==JsonValueKind.Array)foreach(var item in value.EnumerateArray())ValidateKeys(item);
    }
    private static string Token(HttpContext context)
    {
        var auth=context.Request.Headers.Authorization.ToString();
        if(!auth.StartsWith("Bearer ",StringComparison.Ordinal)||auth.Length!=71)throw new AuthenticationException();
        return auth[7..];
    }
    public static async Task Invoke(HttpContext context,Func<Task> next,CommerceDatabase database,AccountService accounts)
    {
        var route=(context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText;
        if(route is null){await next();return;}
        if(route is "/api/health" or "/api/shutdown" or "/api/security/status" or "/api/security/setup" or "/api/security/login") {await next();return;}
        var token=Token(context);var user=accounts.Authenticate(token);var (permission,fullScope)=EndpointPermissions.Resolve(context.Request.Method,route);
        JsonObject? body=null;MemoryStream? rewritten=null;
        if(context.Request.Method is "POST" or "PUT")
        {
            if(context.Request.ContentLength is >0 || context.Request.Headers.ContainsKey("Transfer-Encoding"))
            {
                using var document=await JsonDocument.ParseAsync(context.Request.Body,new JsonDocumentOptions{MaxDepth=32});
                ValidateKeys(document.RootElement);
                var node=JsonNode.Parse(document.RootElement.GetRawText(),new JsonNodeOptions{PropertyNameCaseInsensitive=true},new JsonDocumentOptions{MaxDepth=32});
                body=node as JsonObject??throw new BusinessRuleException("请求正文必须为对象。");
                // Trust the authenticated account, never a client-supplied actor name. Keep the stamp stable across retries.
                var actors=body.Select(p=>p.Key).Where(k=>k.Equals("actor",StringComparison.OrdinalIgnoreCase)).ToArray();
                if(actors.Length>1)throw new BusinessRuleException("操作人字段不能重复。");
                if(actors.Length==1){body.Remove(actors[0]);body["actor"]=AccountService.Actor(user);}
                var bytes=Encoding.UTF8.GetBytes(body.ToJsonString(ErpJson.Options));rewritten=new(bytes);context.Request.Body=rewritten;context.Request.ContentLength=bytes.Length;
            }
        }
        var path=context.Request.Path.Value??route;
        var operation=String(body?["operationId"])??"";
        if(operation.Length>100)throw new BusinessRuleException("操作标识过长。");
        SecurityContext.Current=new(AccountService.TokenHash(token),user,permission,path,context.Request.Method,operation,fullScope,(db,u)=>CheckScope(db,u,context,route,body));
        try {database.Read(_=>true);await next();}
        catch(AccessDeniedException)
        {
            SecurityContext.Current=null;accounts.Denied(user.Id,user.Username,path);throw;
        }
        finally {SecurityContext.Current=null;rewritten?.Dispose();}
    }
    private static void CheckScope(DbSession db,AccountInfo user,HttpContext context,string route,JsonObject? body)
    {
        string? Value(JsonNode? node,string key)=>String((node as JsonObject)?[key]);
        JsonArray Array(string key)=>body?[key] as JsonArray??throw new BusinessRuleException("缺少有效的 "+key+" 集合。");
        var id=context.Request.RouteValues.GetValueOrDefault("id")?.ToString();
        var post=context.Request.Method=="POST";
        if(route.StartsWith("/api/tasks/{kind}/{id}",StringComparison.Ordinal))
        {
            if(!Enum.TryParse<WorkTaskKind>(context.Request.RouteValues["kind"]?.ToString(),true,out var kind)||!Enum.IsDefined(kind))throw new BusinessRuleException("任务类型无效。");
            WorkTaskService.CheckAccess(db,user,kind,id??"",post);
        }
        void Order(string? value)=>DataAccessScope.Order(db,user,value);
        void Case(string? value)=>DataAccessScope.Case(db,user,value);
        if(route.StartsWith("/api/orders/{id}",StringComparison.Ordinal)||route.StartsWith("/api/after-sales/orders/{id}",StringComparison.Ordinal)||route.StartsWith("/api/warehouse/orders/{id}",StringComparison.Ordinal)||route.StartsWith("/api/finance/orders/{id}",StringComparison.Ordinal))Order(id);
        if(route.StartsWith("/api/after-sales/cases/{id}",StringComparison.Ordinal))Case(id);
        if(route=="/api/after-sales/cases"&&post)Order(Value(body,"orderId"));
        if(route=="/api/orders"&&post||route.EndsWith("/correct",StringComparison.Ordinal))
        {var order=body?["order"] as JsonObject??throw new BusinessRuleException("缺少订单对象。");DataAccessScope.Shop(user,Value(order,"tenantId"),Value(order,"shopId"));}
        if(route=="/api/samples/{id}")
        {var sample=SeedData.Samples().FirstOrDefault(s=>s.Descriptor.Id==id);if(sample is not null)DataAccessScope.Shop(user,sample.Order.TenantId,sample.Order.ShopId);}
        if(route.StartsWith("/api/warehouse/shipments/{id}",StringComparison.Ordinal))DataAccessScope.Shipment(db,user,id);
        if(route=="/api/warehouse/shipments"&&post)foreach(var line in Array("lines"))Order(Value(line,"orderId"));
        if(route=="/api/warehouse/merge"&&post)foreach(var shipment in Array("shipments"))DataAccessScope.Shipment(db,user,Value(shipment,"id"));
        if(route=="/api/warehouse/deliveries/{id}/retry")
        {var shipment=db.Scalar<string>("SELECT ShipmentId FROM DeliveryJobs WHERE Id=$id",("id",id));if(shipment is null)throw new AccessDeniedException();DataAccessScope.Shipment(db,user,shipment);}
        if(route.StartsWith("/api/finance/batches/{id}",StringComparison.Ordinal))DataAccessScope.Batch(db,user,id);
        if(route.StartsWith("/api/finance/rows/{id}",StringComparison.Ordinal))
        {
            DataAccessScope.BillRow(db,user,id);
            if(post){Order(Value(body,"orderId"));var caseId=Value(body,"caseId");if(!string.IsNullOrEmpty(caseId))Case(caseId);}
        }
        if(post&&(route=="/api/finance/preview"||route=="/api/finance/batches"))
        {
            var input=(route=="/api/finance/preview"?body:body?["input"]) as JsonObject??throw new BusinessRuleException("缺少账单对象。");
            var shop=Value(input,"shopId");if(string.IsNullOrWhiteSpace(shop))throw new BusinessRuleException("缺少账单所属店铺。");
            DataAccessScope.Shop(user,user.TenantId,shop);
        }
        if(route=="/api/finance/costs/{line}/{sku}")
        {var order=db.Scalar<string>("SELECT OrderId FROM ShipmentLines WHERE Id=$id",("id",context.Request.RouteValues["line"]?.ToString()));if(order is null)throw new AccessDeniedException();Order(order);}
        if(route=="/api/finance/costs"&&context.Request.Query.TryGetValue("order",out var orderQuery)&&!string.IsNullOrEmpty(orderQuery))Order(orderQuery.ToString());
        if(route.StartsWith("/api/finance/exports/{id}",StringComparison.Ordinal))DataAccessScope.Export(db,user,id);
        if(route=="/api/finance/exports"&&post)foreach(var order in Array("orderIds"))Order(String(order));
    }
}
