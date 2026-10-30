using CommerceErp.Core;
using CommerceErp.Infrastructure;
namespace CommerceErp.Api;
public static class InventoryDocumentEndpoints
{
    public static void Map(WebApplication app){
        app.MapGet("/api/inventory-documents/by-code/{code}",(string code,InventoryDocumentService s)=>s.GetBalanceByCode(code));
        app.MapGet("/api/inventory-documents/audit",(InventoryDocumentService s)=>s.Audit());
        app.MapGet("/api/inventory-documents",(InventoryDocumentService s,int page=1,int size=50,string search="",string state="")=>s.List(page,size,search,state));
        app.MapGet("/api/inventory-documents/balance/{sku}",(string sku,InventoryDocumentService s)=>s.GetBalance(sku));
        app.MapGet("/api/inventory-documents/{id}",(string id,InventoryDocumentService s)=>s.Details(id));
        app.MapPost("/api/inventory-documents",(InventoryDocumentRequest r,InventoryDocumentService s)=>s.Create(r));
        app.MapPost("/api/inventory-documents/{id}/edit",(string id,EditInventoryDocumentRequest r,InventoryDocumentService s)=>s.Edit(id,r));
        app.MapPost("/api/inventory-documents/{id}/void",(string id,InventoryDocumentAction r,InventoryDocumentService s)=>s.Void(id,r));
        app.MapPost("/api/inventory-documents/{id}/approve",(string id,InventoryDocumentAction r,InventoryDocumentService s)=>s.Approve(id,r));
        app.MapPost("/api/inventory-documents/{id}/reverse",(string id,ReverseInventoryDocumentRequest r,InventoryDocumentService s)=>s.Reverse(id,r));
    }
}
