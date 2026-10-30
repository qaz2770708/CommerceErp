using CommerceErp.Core;
using System.Data;
using System.Reflection;
using Microsoft.Data.SqlClient;

namespace CommerceErp.Infrastructure;

public sealed partial class CommerceDatabase
{
    public bool IsSqlServer { get; }
    public string ProviderLabel => IsSqlServer ? "SQL Server · 共享事务" : "SQLite · 本机事务";
    public string InstanceId => Read(db => db.Scalar<string>("SELECT Value FROM SystemMetadata WHERE Name='InstanceId'"));
    public const int SchemaVersion = 10;
    // Opening a shared database never creates, seeds, resets or silently upgrades it.
    private CommerceDatabase(string connectionString, bool sqlServer)
    {
        IsSqlServer = sqlServer; _connectionString = ValidateSqlConnection(connectionString); DatabasePath = "";
        Read(db =>
        {
            if (db.Scalar<long>("SELECT MAX(Version) FROM SchemaMigrations") != SchemaVersion ||
                !Guid.TryParseExact(db.Scalar<string>("SELECT Value FROM SystemMetadata WHERE Name='InstanceId'"), "N", out _))
                throw new InvalidDataException("共享数据库版本或身份无效，请先运行数据库工具初始化或迁移。");
            return true;
        });
    }
    public static CommerceDatabase SqlServer(string connectionString) => new(connectionString, true);
    public static void UpgradeAccounts(string connectionString,string expectedDatabase)
    {
        using var connection=OpenMigrationTarget(connectionString,expectedDatabase);
        using var transaction=connection.BeginTransaction();Lock(connection,transaction,false);
        var db=new DbSession(connection,transaction,true);
        var version=db.Scalar<long>("SELECT MAX(Version) FROM SchemaMigrations");
        if(version is 6 or 7 or 8 or 9 or 10){transaction.Commit();return;}
        if(version!=5||!Guid.TryParseExact(db.Scalar<string>("SELECT Value FROM SystemMetadata WHERE Name='InstanceId'"),"N",out _))
            throw new InvalidDataException("账号升级仅接受有有效身份的版本 5 数据库。");
        using var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("CommerceErp.Infrastructure.Migrations.SqlServer.006_accounts.sql")!;
        using var reader=new StreamReader(stream);db.Execute(reader.ReadToEnd());
        db.Execute("INSERT INTO SchemaMigrations VALUES(6,$now)",("now",Now()));transaction.Commit();
    }
    public static void UpgradeTasks(string connectionString,string expectedDatabase)
    {
        using var connection=OpenMigrationTarget(connectionString,expectedDatabase);using var transaction=connection.BeginTransaction();Lock(connection,transaction,false);
        var db=new DbSession(connection,transaction,true);var version=db.Scalar<long>("SELECT MAX(Version) FROM SchemaMigrations");
        if(version is 7 or 8 or 9 or 10){transaction.Commit();return;}
        if(version!=6||!Guid.TryParseExact(db.Scalar<string>("SELECT Value FROM SystemMetadata WHERE Name='InstanceId'"),"N",out _))throw new InvalidDataException("任务升级仅接受有有效身份的版本 6 数据库。");
        using var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("CommerceErp.Infrastructure.Migrations.SqlServer.007_tasks.sql")!;
        using var reader=new StreamReader(stream);db.Execute(reader.ReadToEnd());db.Execute("INSERT INTO SchemaMigrations VALUES(7,$now)",("now",Now()));transaction.Commit();
    }
    public static void UpgradeMaster(string connectionString,string expectedDatabase)
    {
        using var connection=OpenMigrationTarget(connectionString,expectedDatabase);using var transaction=connection.BeginTransaction();Lock(connection,transaction,false);
        var db=new DbSession(connection,transaction,true);var version=db.Scalar<long>("SELECT MAX(Version) FROM SchemaMigrations");
        if(version is 8 or 9 or 10){transaction.Commit();return;}
        if(version!=7||!Guid.TryParseExact(db.Scalar<string>("SELECT Value FROM SystemMetadata WHERE Name='InstanceId'"),"N",out _))throw new InvalidDataException("主数据升级仅接受有有效身份的版本 7 数据库。");
        using var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("CommerceErp.Infrastructure.Migrations.SqlServer.008_master.sql")!;
        using var reader=new StreamReader(stream);db.Execute(reader.ReadToEnd());db.Execute("INSERT INTO SchemaMigrations VALUES(8,$now)",("now",Now()));transaction.Commit();
    }
    public static void UpgradeReview(string connectionString,string expectedDatabase){using var c=OpenMigrationTarget(connectionString,expectedDatabase);using var tx=c.BeginTransaction();Lock(c,tx,false);var db=new DbSession(c,tx,true);var v=db.Scalar<long>("SELECT MAX(Version) FROM SchemaMigrations");if(v is 9 or 10){tx.Commit();return;}if(v!=8||!Guid.TryParseExact(db.Scalar<string>("SELECT Value FROM SystemMetadata WHERE Name='InstanceId'"),"N",out _))throw new InvalidDataException("审批升级仅接受身份有效的 schema 8。");using var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("CommerceErp.Infrastructure.Migrations.SqlServer.009_review.sql")!;using var reader=new StreamReader(stream);db.Execute(reader.ReadToEnd());InitializeReview(db);db.Execute("INSERT INTO SchemaMigrations VALUES(9,$now)",("now",Now()));tx.Commit();}
    public static void UpgradeInventory(string connectionString,string expectedDatabase){using var c=OpenMigrationTarget(connectionString,expectedDatabase);using var tx=c.BeginTransaction();Lock(c,tx,false);var db=new DbSession(c,tx,true);var v=db.Scalar<long>("SELECT MAX(Version) FROM SchemaMigrations");if(v==10){tx.Commit();return;}if(v!=9||!Guid.TryParseExact(db.Scalar<string>("SELECT Value FROM SystemMetadata WHERE Name='InstanceId'"),"N",out _))throw new InvalidDataException("库存升级仅接受身份有效的schema9。");using var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("CommerceErp.Infrastructure.Migrations.SqlServer.010_inventory.sql")!;using var reader=new StreamReader(stream);db.Execute(reader.ReadToEnd());db.Execute("INSERT INTO SchemaMigrations VALUES(10,$now)",("now",Now()));tx.Commit();}
    public static string ValidateSqlConnection(string connectionString)
    {
        var cs = new SqlConnectionStringBuilder(connectionString);
        if (string.IsNullOrWhiteSpace(cs.DataSource) || string.IsNullOrWhiteSpace(cs.InitialCatalog) ||
            new[] { "master", "model", "msdb", "tempdb" }.Contains(cs.InitialCatalog, StringComparer.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(cs.AttachDBFilename)) throw new InvalidDataException("须指定独立业务数据库；禁止系统库及 AttachDBFilename。");
        if (cs.TrustServerCertificate || cs.Encrypt == SqlConnectionEncryptOption.Optional)
            throw new InvalidDataException("SQL Server 连接必须启用 Encrypt=True 并验证服务端证书。");
        cs.ApplicationName = "CommerceErp.Stage6"; cs.ConnectTimeout = 15;
        return cs.ConnectionString;
    }
    internal static void Lock(SqlConnection connection, SqlTransaction transaction, bool read)
    {
        using var cmd = connection.CreateCommand(); cmd.Transaction = transaction;
        cmd.CommandText = "DECLARE @r int; EXEC @r=sys.sp_getapplock @Resource=N'CommerceErp.Database.v1', @LockMode=@mode, @LockOwner='Transaction', @LockTimeout=10000; SELECT @r;";
        cmd.Parameters.AddWithValue("@mode", read ? "Shared" : "Exclusive");
        if (Convert.ToInt32(cmd.ExecuteScalar()) < 0) throw new DatabaseBusyException();
    }
    private T RunSqlServer<T>(Func<DbSession, T> action, bool read)
    {
        try
        {
            using var connection = new SqlConnection(_connectionString); connection.Open();
            using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
            Lock(connection, transaction, read);
            var result = SecurityContext.Run(new(connection, transaction, true), read, action); transaction.Commit(); return result;
        }
        catch (SqlException e) when (e.Number is 1205 or 1222 or -2) { throw new DatabaseBusyException(); }
    }
    internal static void CreateSqlSchema(SqlConnection connection, SqlTransaction transaction)
    {
        var db = new DbSession(connection, transaction, true);
        if (db.Scalar<int>("SELECT HAS_PERMS_BY_NAME(DB_NAME(),'DATABASE','VIEW DEFINITION')") != 1)
            throw new InvalidDataException("迁移账户须有数据库 VIEW DEFINITION 权限，才能完整核对目标为空。");
        if (db.Scalar<int>("SELECT COUNT(*) FROM sys.tables WHERE is_ms_shipped=0") != 0)
            throw new InvalidDataException("目标数据库必须为空；不会覆盖任何已有表。");
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("CommerceErp.Infrastructure.Migrations.SqlServer.001_schema.sql")!;
        using var reader = new StreamReader(stream); db.Execute(reader.ReadToEnd());
    }
    public static void InitializeSqlServerDemo(string connectionString, string expectedDatabase)
    {
        using var connection = OpenMigrationTarget(connectionString, expectedDatabase);
        using var transaction = connection.BeginTransaction(); Lock(connection, transaction, false);
        CreateSqlSchema(connection, transaction); var db = new DbSession(connection, transaction, true);
        Seed(db, null);
        db.Execute("INSERT INTO MasterWorkspace VALUES(1,'Demo',1); INSERT INTO MasterSkuData SELECT Id,N'件',1 FROM Skus; INSERT INTO MasterShopData SELECT Id,Id,1,1 FROM Shops;");
        db.Execute("INSERT INTO SkuBarcodes SELECT TenantId,Code,Id,1 FROM Skus WHERE Enabled=1 AND ComponentsJson='[]'");
        db.Execute("INSERT INTO QuarantineBalances(TenantId,OwnerId,WarehouseId,SkuId) SELECT TenantId,OwnerId,WarehouseId,SkuId FROM InventoryBalances");
        InitializeReview(db); db.Execute("INSERT INTO SchemaMigrations VALUES(10,$now)", ("now", Now()));
        db.Execute("INSERT INTO SystemMetadata VALUES('InstanceId',$id)", ("id", Guid.NewGuid().ToString("N")));
        transaction.Commit();
    }
    public static void InitializeSqlServerBusiness(string connectionString,string expectedDatabase,WorkspaceInitialization initialization)
    {
        using var connection=OpenMigrationTarget(connectionString,expectedDatabase);using var transaction=connection.BeginTransaction();Lock(connection,transaction,false);CreateSqlSchema(connection,transaction);var db=new DbSession(connection,transaction,true);SeedBusiness(db,initialization);
        db.Execute("INSERT INTO MasterWorkspace VALUES(1,'Business',1)");InitializeReview(db);db.Execute("INSERT INTO SchemaMigrations VALUES(10,$now)",("now",Now()));db.Execute("INSERT INTO SystemMetadata VALUES('InstanceId',$id)",("id",Guid.NewGuid().ToString("N")));transaction.Commit();
    }
    internal static SqlConnection OpenMigrationTarget(string connectionString, string expectedDatabase)
    {
        var cs = ValidateSqlConnection(connectionString);
        if (string.IsNullOrWhiteSpace(expectedDatabase) || !string.Equals(new SqlConnectionStringBuilder(cs).InitialCatalog, expectedDatabase, StringComparison.Ordinal))
            throw new InvalidDataException("--database 必须与连接串目标数据库完全一致。");
        var connection = new SqlConnection(cs);
        try { connection.Open(); return connection; } catch { connection.Dispose(); throw; }
    }
}
