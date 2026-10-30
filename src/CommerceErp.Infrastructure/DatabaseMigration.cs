using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;

namespace CommerceErp.Infrastructure;

public sealed record TableVerification(string Table, long Rows, string Sha256);
public sealed record MigrationReport(string Status, string Database, string SourceSha256, string InstanceId, List<TableVerification> Tables, string[] ExcludedTables);

public sealed partial class CommerceDatabase
{
    public static IDisposable LockMigrationSource(string sourceFile)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(sourceFile))!;
        var handles = new List<FileStream>();
        try
        {
            foreach (var file in new[] { "desktop.lock", "service.lock" })
                handles.Add(new FileStream(Path.Combine(directory, file), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
            var journal = Path.Combine(directory, "pending-commands.json");
            if (File.Exists(journal))
            {
                using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(journal));
                if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object || document.RootElement.EnumerateObject().Any())
                    throw new InvalidDataException("源工作区仍有待重试操作，请先在旧客户端核清结果。迁移未开始。");
            }
            return new MigrationLease(handles);
        }
        catch
        { foreach (var handle in handles) handle.Dispose(); throw; }
    }
    private sealed class MigrationLease(List<FileStream> handles) : IDisposable
    { public void Dispose() { foreach (var handle in handles) handle.Dispose(); } }
    public static string CreateVerifiedSnapshot(string sourceFile, string snapshotDirectory)
    {
        sourceFile = Path.GetFullPath(sourceFile); snapshotDirectory = Path.GetFullPath(snapshotDirectory);
        if (!File.Exists(sourceFile)) throw new FileNotFoundException("源数据库不存在。");
        if (Directory.Exists(snapshotDirectory)) throw new InvalidDataException("快照目录必须尚不存在，以免覆盖文件。");
        _ = Provider.Value; Directory.CreateDirectory(snapshotDirectory);
        using (var source = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = sourceFile, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
        using (var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(snapshotDirectory, "commerce.db"), Pooling = false }.ToString()))
        { source.Open(); target.Open(); source.BackupDatabase(target); }
        var database = new CommerceDatabase(snapshotDirectory); // Upgrade the copy only; source stays untouched.
        database.Read(db =>
        {
            if (db.Scalar<string>("PRAGMA integrity_check") != "ok" || db.Query("PRAGMA foreign_key_check", r => 1).Count != 0)
                throw new InvalidDataException("源数据库完整性检查失败。");
            return true;
        });
        var service = new OrderService(database);
        if (!service.CheckConsistency().Passed || !service.AuditWarehouse().Passed || !service.AuditAfterSales().Passed || !service.AuditFinance().Passed)
            throw new InvalidDataException("源数据业务一致性检查失败，不能迁移。");
        return database.DatabasePath;
    }
    public static MigrationReport MigrateToSqlServer(string snapshotFile, string connectionString, string expectedDatabase)
    {
        _ = Provider.Value;
        using var source = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.GetFullPath(snapshotFile), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        source.Open(); using var sourceTransaction = source.BeginTransaction(deferred: true);
        using var target = OpenMigrationTarget(connectionString, expectedDatabase);
        using var transaction = target.BeginTransaction(); Lock(target, transaction, false); CreateSqlSchema(target, transaction);
        var db = new DbSession(target, transaction, true);
        var remaining = db.Query("SELECT name FROM sys.tables WHERE is_ms_shipped=0 ORDER BY name", r => r.GetString(0)).ToHashSet(StringComparer.Ordinal);
        var dependencies = db.Query("SELECT OBJECT_NAME(parent_object_id), OBJECT_NAME(referenced_object_id) FROM sys.foreign_keys", r => (Child: r.GetString(0), Parent: r.GetString(1)));
        var verified = new List<TableVerification>();
        while (remaining.Count > 0)
        {
            var table = remaining.Order(StringComparer.Ordinal).FirstOrDefault(t => !dependencies.Any(d => d.Child == t && remaining.Contains(d.Parent) && d.Child != d.Parent))
                ?? throw new InvalidDataException("数据库外键依赖存在环。");
            remaining.Remove(table);
            if (table is "SystemMetadata" or "SecuritySessions") continue; // Every target receives a fresh identity, preventing stale client replay.
            using var sourceCommand = source.CreateCommand(); sourceCommand.Transaction = sourceTransaction;
            sourceCommand.CommandText = "SELECT * FROM [" + table + "]";
            using (var reader = sourceCommand.ExecuteReader())
            using (var copy = new SqlBulkCopy(target, SqlBulkCopyOptions.KeepIdentity | SqlBulkCopyOptions.CheckConstraints | SqlBulkCopyOptions.KeepNulls, transaction))
            {
                copy.DestinationTableName = "dbo.[" + table + "]"; copy.BulkCopyTimeout = 120;
                for (int i = 0; i < reader.FieldCount; i++) copy.ColumnMappings.Add(reader.GetName(i), reader.GetName(i));
                copy.WriteToServer(reader);
            }
            var expected = Digest(sourceCommand, table);
            using var targetCommand = db.Command("SELECT * FROM [" + table + "]");
            var actual = Digest(targetCommand, table);
            if (expected != actual) throw new InvalidDataException("迁移校验失败：" + table + "；目标事务已回滚。");
            verified.Add(actual);
        }
        var identity = Guid.NewGuid().ToString("N");
        db.Execute("INSERT INTO SystemMetadata VALUES('InstanceId',$id)", ("id", identity));
        if (db.Scalar<int>("SELECT MAX(Version) FROM SchemaMigrations") != SchemaVersion) throw new InvalidDataException("必须使用已验证的最新版本快照。");
        using var snapshotStream = File.OpenRead(snapshotFile);
        var sourceHash = Convert.ToHexString(SHA256.HashData(snapshotStream));
        transaction.Commit(); sourceTransaction.Commit();
        return new("Committed", expectedDatabase, sourceHash, identity, verified, ["SystemMetadata (new identity)", "SecuritySessions (login again)"]);
    }
    private static TableVerification Digest(System.Data.Common.DbCommand command, string table)
    {
        using var reader = command.ExecuteReader(); var hashes = new List<string>();
        while (reader.Read())
        {
            var values = Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? null : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture)).ToArray();
            hashes.Add(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Json(values)))));
        }
        hashes.Sort(StringComparer.Ordinal);
        return new(table, hashes.Count, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', hashes)))));
    }
}
