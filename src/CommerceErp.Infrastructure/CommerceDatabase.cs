using System.Globalization;
using System.Reflection;
using System.Text.Json;
using CommerceErp.Core;
using Microsoft.Data.Sqlite;

namespace CommerceErp.Infrastructure;

public sealed class DatabaseBusyException() : Exception("数据库正在处理其他事务，请使用相同操作标识重试。");

public sealed partial class CommerceDatabase
{
    private static readonly Lazy<bool> Provider = new(() => { SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_e_sqlite3()); return true; });
    private readonly string _connectionString;
    public string DatabasePath { get; }
    public CommerceDatabase(string directory,WorkspaceInitialization? initialization=null)
    {
        _ = Provider.Value;
        Directory.CreateDirectory(directory);
        DatabasePath = Path.Combine(Path.GetFullPath(directory), "commerce.db");
        _connectionString = new SqliteConnectionStringBuilder
        { DataSource = DatabasePath, ForeignKeys = true, DefaultTimeout = 10, Pooling = false }.ToString();
        using (var connection = Open())
        {
            using var command = connection.CreateCommand(); command.CommandText = "PRAGMA journal_mode=DELETE;"; command.ExecuteNonQuery();
        }
        Write(db =>
        {
            db.Execute("CREATE TABLE IF NOT EXISTS SchemaMigrations(Version INTEGER PRIMARY KEY, AppliedAt TEXT NOT NULL)");
            var version = db.Scalar<long>("SELECT COALESCE(MAX(Version),0) FROM SchemaMigrations");
            if(initialization is not null&&version!=0)throw new InvalidDataException("正式工作区必须使用全新数据库，禁止转换已有演示或业务数据。");
            if (version > 10) throw new InvalidDataException("数据库版本高于当前应用，不能降级打开。");
            if (version == 0)
            {
                using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("CommerceErp.Infrastructure.Migrations.001_business.sql")!;
                using var reader = new StreamReader(stream); db.Execute(reader.ReadToEnd());
                WorkspaceState? legacy = null;
                var legacyPath = Path.Combine(directory, "workspace.json");
                if (File.Exists(legacyPath))
                {
                    legacy = FromJson<WorkspaceState>(File.ReadAllText(legacyPath));
                    if (legacy.SchemaVersion != 1 || legacy.Readiness is null || legacy.Mappings is null || legacy.Reports is null)
                        throw new InvalidDataException("第一阶段工作区格式不兼容，原文件未被修改。");
                }
                if(initialization is null)Seed(db,legacy);
                else { if(legacy is not null)throw new InvalidDataException("不能把已有工作区作为正式初始化目标。"); SeedBusiness(db,initialization); }
                db.Execute("INSERT INTO SchemaMigrations VALUES(1,$now)", ("now", Now()));
            }
            if (version < 2)
            {
                using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("CommerceErp.Infrastructure.Migrations.002_warehouse.sql")!;
                using var reader = new StreamReader(stream); db.Execute(reader.ReadToEnd());
                db.Execute("INSERT INTO SchemaMigrations VALUES(2,$now)", ("now", Now()));
            }
            if (version < 3)
            {
                using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("CommerceErp.Infrastructure.Migrations.003_after_sales.sql")!;
                using var reader = new StreamReader(stream); db.Execute(reader.ReadToEnd());
                db.Execute("INSERT INTO SchemaMigrations VALUES(3,$now)", ("now", Now()));
            }
            if (version < 4)
            {
                using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("CommerceErp.Infrastructure.Migrations.004_finance.sql")!;
                using var reader = new StreamReader(stream); db.Execute(reader.ReadToEnd());
                db.Execute("INSERT INTO SchemaMigrations VALUES(4,$now)", ("now", Now()));
            }
            if (version < 5)
            {
                db.Execute("CREATE TABLE SystemMetadata(Name TEXT PRIMARY KEY, Value TEXT NOT NULL)");
                db.Execute("INSERT INTO SystemMetadata VALUES('InstanceId',$id)", ("id", Guid.NewGuid().ToString("N")));
                db.Execute("INSERT INTO SchemaMigrations VALUES(5,$now)", ("now", Now()));
            }
            if (version < 6)
            {
                using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("CommerceErp.Infrastructure.Migrations.006_accounts.sql")!;
                using var reader = new StreamReader(stream); db.Execute(reader.ReadToEnd());
                db.Execute("INSERT INTO SchemaMigrations VALUES(6,$now)", ("now", Now()));
            }
            if(version<7)
            {
                using var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("CommerceErp.Infrastructure.Migrations.007_tasks.sql")!;
                using var reader=new StreamReader(stream);db.Execute(reader.ReadToEnd());
                db.Execute("INSERT INTO SchemaMigrations VALUES(7,$now)",("now",Now()));
            }
            if(version<8)
            {
                using var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("CommerceErp.Infrastructure.Migrations.008_master.sql")!;
                using var reader=new StreamReader(stream);db.Execute(reader.ReadToEnd());db.Execute("INSERT INTO SchemaMigrations VALUES(8,$now)",("now",Now()));
                if(initialization is not null)db.Execute("UPDATE MasterWorkspace SET Mode='Business' WHERE Id=1");
            }
            if(version<9){using var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("CommerceErp.Infrastructure.Migrations.009_review.sql")!;using var reader=new StreamReader(stream);db.Execute(reader.ReadToEnd());InitializeReview(db);db.Execute("INSERT INTO SchemaMigrations VALUES(9,$now)",("now",Now()));}
            if(version<10){using var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("CommerceErp.Infrastructure.Migrations.010_inventory.sql")!;using var reader=new StreamReader(stream);db.Execute(reader.ReadToEnd());db.Execute("INSERT INTO SchemaMigrations VALUES(10,$now)",("now",Now()));}
            return true;
        });
    }
    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString); connection.Open();
        connection.CreateFunction<string, string, long>("CHARINDEX", (needle, haystack) => haystack.IndexOf(needle, StringComparison.Ordinal) + 1, isDeterministic: true);
        using var command = connection.CreateCommand(); command.CommandText = "PRAGMA synchronous=FULL;"; command.ExecuteNonQuery();
        return connection;
    }
    public T Write<T>(Func<DbSession, T> action) => Run(action, false);
    public T Read<T>(Func<DbSession, T> action) => Run(action, true);
    private T Run<T>(Func<DbSession, T> action, bool deferred)
    {
        if (IsSqlServer) return RunSqlServer(action, deferred);
        try
        {
            using var connection = Open(); using var transaction = connection.BeginTransaction(deferred: deferred);
            var result = SecurityContext.Run(new(connection, transaction), deferred, action); transaction.Commit(); return result;
        }
        catch (SqliteException e) when (e.SqliteErrorCode is 5 or 6) { throw new DatabaseBusyException(); }
    }
    public static string Json<T>(T value) => JsonSerializer.Serialize(value, ErpJson.Options);
    public static T FromJson<T>(string value) => JsonSerializer.Deserialize<T>(value, ErpJson.Options) ?? throw new InvalidDataException("数据库 JSON 记录为空。");
    public static string Now() => DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
    public static void DemoTenant(string tenant)
    { if (tenant != SeedData.Tenant) throw new BusinessRuleException("此本地版本仅开放 demo-enterprise 模拟租户。"); }
    public static string Tenant(DbSession db)=>db.TenantId;
    public static string OwnerOf(DbSession db)=>db.OwnerId;
    public static string WarehouseOf(DbSession db)=>db.WarehouseId;
    public static bool BusinessMode(DbSession db)=>db.Scalar<string>("SELECT Mode FROM MasterWorkspace WHERE Id=1")=="Business";
    public static void RequireTenant(DbSession db,string tenant){if(tenant!=Tenant(db))throw new BusinessRuleException("企业标识与当前工作区不符。");}
    public static List<string> ShopIds(DbSession db)=>db.Query("SELECT Id FROM Shops WHERE TenantId=$t ORDER BY Id",r=>r.GetString(0),("t",Tenant(db)));
    public static Catalog ReadCatalog(DbSession db)=>new(
        db.Query("SELECT Id,Code,Name,Enabled,ComponentsJson FROM Skus WHERE TenantId=$t ORDER BY Code",r=>new InternalSku(r.GetString(0),r.GetString(1),r.GetString(2),r.GetInt64(3)==1,FromJson<List<SkuComponent>>(r.GetString(4))),("t",Tenant(db))),
        db.Query("SELECT m.TenantId,m.Platform,m.ShopId,m.ProductId,m.VariantId,m.SkuId,m.MultiplierUnits,m.Version,COALESCE(x.Enabled,1) FROM SkuMappings m LEFT JOIN MasterMappingState x ON x.TenantId=m.TenantId AND x.Platform=m.Platform AND x.ShopId=m.ShopId AND x.ProductId=m.ProductId AND x.VariantId=m.VariantId WHERE m.TenantId=$t ORDER BY m.Platform,m.ShopId,m.ProductId,m.VariantId",r=>new SkuMapping(r.GetString(0),Enum.Parse<Platform>(r.GetString(1)),r.GetString(2),r.GetString(3),r.GetString(4),r.GetString(5),BusinessUnits.FromQuantity(r.GetInt64(6)),r.GetInt32(7),r.GetInt64(8)==1),("t",Tenant(db))));
    private static void SeedBusiness(DbSession db,WorkspaceInitialization init)
    {
        MasterDataService.Text(init.EnterpriseName,"企业名称",100);MasterDataService.Text(init.WarehouseName,"仓库名称",100);
        db.Execute("INSERT INTO Tenants VALUES($id,$name)",("id","enterprise-"+Guid.NewGuid().ToString("N")),("name",init.EnterpriseName.Trim()));
        db.Execute("INSERT INTO Warehouses VALUES($t,$owner,$id,$name)",("t",Tenant(db)),("owner","owner-"+Guid.NewGuid().ToString("N")),("id","warehouse-"+Guid.NewGuid().ToString("N")),("name",init.WarehouseName.Trim()));
        foreach(var item in SeedData.Readiness())db.Execute("INSERT INTO Readiness VALUES($p,$v,$items)",("p",item.Platform.ToString()),("v",item.Revision),("items",Json(item.Items)));
    }
    private static void Seed(DbSession db, WorkspaceState? legacy)
    {
        db.Execute("INSERT INTO Tenants VALUES($t,'模拟企业')", ("t", SeedData.Tenant));
        foreach (var p in SeedData.Platforms)
            db.Execute("INSERT INTO Shops VALUES($t,$p,$s,$n)", ("t", SeedData.Tenant), ("p", p.Id.ToString()), ("s", SeedData.Shop(p.Id)), ("n", p.Name + "示例店"));
        db.Execute("INSERT INTO Warehouses VALUES($t,$o,$w,'示例主仓')", ("t", SeedData.Tenant), ("o", BusinessUnits.Owner), ("w", BusinessUnits.Warehouse));
        foreach (var sku in SeedData.Skus())
        {
            db.Execute("INSERT INTO Skus VALUES($t,$id,$code,$name,$enabled,$components)", ("t", SeedData.Tenant), ("id", sku.Id), ("code", sku.Code), ("name", sku.Name), ("enabled", sku.Enabled ? 1 : 0), ("components", Json(sku.Components)));
            if (!sku.Enabled || sku.Components.Count > 0) continue;
            db.Execute("INSERT INTO InventoryBalances(TenantId,OwnerId,WarehouseId,SkuId,OnHandUnits) VALUES($t,$o,$w,$s,100000)",
                ("t", SeedData.Tenant), ("o", BusinessUnits.Owner), ("w", BusinessUnits.Warehouse), ("s", sku.Id));
            db.Execute("""
                INSERT INTO InventoryLedger(TenantId,OwnerId,WarehouseId,SkuId,OperationId,Action,OnHandDelta,ReservedDelta,OnHandAfter,ReservedAfter,FrozenAfter,SafetyAfter,Reason,CreatedAt)
                VALUES($t,$o,$w,$s,'seed-opening','Opening',100000,0,100000,0,0,0,'首次初始化：模拟期初库存 100 件',$now)
                """, ("t", SeedData.Tenant), ("o", BusinessUnits.Owner), ("w", BusinessUnits.Warehouse), ("s", sku.Id), ("now", Now()));
        }
        foreach (var map in legacy?.Mappings ?? SeedData.Mappings())
        {
            DemoTenant(map.TenantId);
            db.Execute("INSERT INTO SkuMappings VALUES($t,$p,$shop,$product,$variant,$sku,$multiplier,$version)",
                ("t", map.TenantId), ("p", map.Platform.ToString()), ("shop", map.ShopId), ("product", map.PlatformProductId),
                ("variant", map.PlatformSkuId), ("sku", map.InternalSkuId), ("multiplier", BusinessUnits.Quantity(map.UnitMultiplier)), ("version", map.Version));
        }
        foreach (var item in legacy?.Readiness ?? SeedData.Readiness())
            db.Execute("INSERT INTO Readiness VALUES($p,$v,$items)", ("p", item.Platform.ToString()), ("v", item.Revision), ("items", Json(item.Items)));
        foreach (var report in (legacy?.Reports ?? []).TakeLast(200))
            db.Execute("INSERT INTO ValidationReports VALUES($id,$at,$json)", ("id", report.ReportId), ("at", report.CreatedAt.ToString("O")), ("json", Json(report)));
    }
}
