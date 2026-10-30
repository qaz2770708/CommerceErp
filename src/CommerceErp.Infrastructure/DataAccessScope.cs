using CommerceErp.Core;

namespace CommerceErp.Infrastructure;

// Called inside the same database transaction as each HTTP operation, including replay.
public static class DataAccessScope
{
    public static void Shop(AccountInfo user,string? tenant,string? shop)
    { if(tenant!=user.TenantId || shop is null || !user.Shops.Contains(shop))throw new AccessDeniedException(); }
    public static void Order(DbSession db,AccountInfo user,string? id)
    {
        if(string.IsNullOrWhiteSpace(id))throw new BusinessRuleException("缺少订单标识。");
        var row=db.Query("SELECT TenantId,ShopId,WarehouseId FROM SalesOrders WHERE Id=$id",r=>(Tenant:r.GetString(0),Shop:r.GetString(1),Warehouse:r.GetString(2)),("id",id)).SingleOrDefault();
        Shop(user,row.Tenant,row.Shop);if(!user.Warehouses.Contains(row.Warehouse))throw new AccessDeniedException();
    }
    public static void Case(DbSession db,AccountInfo user,string? id)
    {
        var order=db.Scalar<string>("SELECT OrderId FROM AfterSaleCases WHERE Id=$id",("id",id));
        if(order is null)throw new AccessDeniedException();Order(db,user,order);
    }
    public static void Shipment(DbSession db,AccountInfo user,string? id)
    {
        var row=db.Query("SELECT TenantId,ShopId,WarehouseId FROM Shipments WHERE Id=$id",r=>(Tenant:r.GetString(0),Shop:r.GetString(1),Warehouse:r.GetString(2)),("id",id)).SingleOrDefault();
        Shop(user,row.Tenant,row.Shop);if(!user.Warehouses.Contains(row.Warehouse))throw new AccessDeniedException();
        var orders=db.Query("SELECT DISTINCT OrderId FROM ShipmentLines WHERE ShipmentId=$id",r=>r.GetString(0),("id",id));
        if(orders.Count==0)throw new AccessDeniedException();foreach(var order in orders)Order(db,user,order);
    }
    public static void Batch(DbSession db,AccountInfo user,string? id)
    {
        var row=db.Query("SELECT TenantId,ShopId FROM BillBatches WHERE Id=$id",r=>(Tenant:r.GetString(0),Shop:r.GetString(1)),("id",id)).SingleOrDefault();Shop(user,row.Tenant,row.Shop);
    }
    public static void BillRow(DbSession db,AccountInfo user,string? id)
    {
        var row=db.Query("SELECT TenantId,ShopId,BatchId,OrderId FROM BillRows WHERE Id=$id",r=>(Tenant:r.GetString(0),Shop:r.GetString(1),Batch:r.GetString(2),Order:r.IsDBNull(3)?null:r.GetString(3)),("id",id)).SingleOrDefault();
        Shop(user,row.Tenant,row.Shop);Batch(db,user,row.Batch);if(row.Order is not null)Order(db,user,row.Order);
    }
    public static void Export(DbSession db,AccountInfo user,string? id)
    {
        var orders=db.Query("SELECT OrderId FROM FinanceExportOrders WHERE ExportId=$id",r=>r.GetString(0),("id",id));
        if(orders.Count==0)throw new AccessDeniedException();foreach(var order in orders)Order(db,user,order);
    }
    public static string ExportsFilter(string alias)=>SecurityContext.Current is null?"":
        " AND EXISTS(SELECT 1 FROM FinanceExportOrders scope_link WHERE scope_link.ExportId="+alias+".Id) AND NOT EXISTS(SELECT 1 FROM FinanceExportOrders scope_link JOIN SalesOrders scope_order ON scope_order.Id=scope_link.OrderId WHERE scope_link.ExportId="+alias+".Id AND NOT EXISTS(SELECT 1 FROM SecurityUserShops scope_grant WHERE scope_grant.UserId=$accessUser AND scope_grant.ShopId=scope_order.ShopId))";
}
