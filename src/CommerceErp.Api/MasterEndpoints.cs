using CommerceErp.Core;
using CommerceErp.Infrastructure;
namespace CommerceErp.Api;
public static class MasterEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/catalog/products",(string shop,string? search,int? page,int? size,MasterDataService s)=>s.Products(shop,search??"",page??1,size??50));
        app.MapGet("/api/master/context",(MasterDataService s)=>s.Context());
        app.MapPost("/api/master/lookup",(MasterLookupRequest r,MasterDataService s)=>s.Lookup(r));
        app.MapGet("/api/master/policy-changes",(MasterDataService s)=>s.PolicyChanges());
        app.MapPost("/api/master/policy-changes/{id}/approve",(string id,ApproveOpeningRequest r,MasterDataService s)=>s.ApprovePolicyChange(id,r));
        app.MapGet("/api/master/skus/{id}",(string id,MasterDataService s)=>s.SkuDetails(id));
        app.MapGet("/api/master/skus/page",(int? page,int? size,string? search,MasterDataService s)=>s.SkuPage(page??1,size??50,search??""));
        app.MapGet("/api/master/mappings/page",(int? page,int? size,string? search,MasterDataService s)=>s.MappingPage(page??1,size??50,search??""));
        app.MapGet("/api/master/openings/page",(int? page,int? size,string? search,MasterDataService s)=>s.OpeningPage(page??1,size??50,search??""));
        app.MapGet("/api/master/unmapped/page",(int? page,int? size,string? search,MasterDataService s)=>s.UnmappedPage(page??1,size??50,search??""));
        app.MapGet("/api/master/opening-policy",(MasterDataService s)=>s.GetOpeningPolicy());
        app.MapPost("/api/master/opening-policy",(SaveOpeningPolicyRequest r,MasterDataService s)=>s.SaveOpeningPolicy(r));
        app.MapPost("/api/master/openings/{id}/edit",(string id,EditOpeningRequest r,MasterDataService s)=>s.EditOpening(id,r));
        app.MapPost("/api/master/openings/{id}/void",(string id,ApproveOpeningRequest r,MasterDataService s)=>s.VoidOpening(id,r));
        app.MapGet("/api/workspace",(MasterDataService s)=>s.Workspace());
        app.MapGet("/api/master",(MasterDataService s)=>s.Snapshot());
        app.MapGet("/api/master/history",(string entity,MasterDataService s)=>s.History(entity));
        app.MapPost("/api/master/workspace",(SaveWorkspaceRequest r,MasterDataService s)=>s.SaveWorkspace(r));
        app.MapPost("/api/master/shops",(SaveShopRequest r,MasterDataService s)=>s.SaveShop(r));
        app.MapPost("/api/master/skus",(SaveSkuRequest r,MasterDataService s)=>s.SaveSku(r));
        app.MapPost("/api/master/mappings/preview",(MappingPreviewRequest r,MasterDataService s)=>s.PreviewMappings(r));
        app.MapPost("/api/master/mappings",(MappingBatchRequest r,MasterDataService s)=>s.SaveMappings(r));
        app.MapGet("/api/master/openings/{id}",(string id,MasterDataService s)=>s.OpeningDetails(id));
        app.MapGet("/api/master/openings",(MasterDataService s)=>s.Openings());
        app.MapPost("/api/master/openings",(OpeningRequest r,MasterDataService s)=>s.CreateOpening(r));
        app.MapPost("/api/master/openings/{id}/approve",(string id,ApproveOpeningRequest r,MasterDataService s)=>s.ApproveOpening(id,r));
    }
}
