using System.Data.Common;
using System.Globalization;

namespace CommerceErp.Infrastructure;

// Normalizes provider numeric types; quantities and money always remain integer based.
public sealed class DbRow(DbDataReader reader)
{
    public string GetString(int i) => reader.GetString(i);
    public long GetInt64(int i) => Convert.ToInt64(reader.GetValue(i), CultureInfo.InvariantCulture);
    public int GetInt32(int i) => checked((int)GetInt64(i));
    public bool IsDBNull(int i) => reader.IsDBNull(i);
}

public sealed class DbSession(DbConnection connection, DbTransaction transaction, bool sqlServer = false)
{
    private string? _tenant,_owner,_warehouse;
    public string TenantId=>_tenant??=Query("SELECT Id FROM Tenants",r=>r.GetString(0)).Single();
    public string OwnerId=>_owner??=Query("SELECT OwnerId FROM Warehouses",r=>r.GetString(0)).Single();
    public string WarehouseId=>_warehouse??=Query("SELECT Id FROM Warehouses",r=>r.GetString(0)).Single();
    public static string SavepointSql(bool sqlServer,string action,string name)
    {
        if(!System.Text.RegularExpressions.Regex.IsMatch(name,"^[A-Za-z][A-Za-z0-9_]{0,30}$"))throw new ArgumentException("保存点名称无效。");
        return (sqlServer,action) switch{(true,"save")=>"SAVE TRANSACTION "+name,(true,"rollback")=>"ROLLBACK TRANSACTION "+name,(true,"release")=>"",(false,"save")=>"SAVEPOINT "+name,(false,"rollback")=>"ROLLBACK TO "+name,(false,"release")=>"RELEASE "+name,_=>throw new ArgumentException("保存点操作无效。")};
    }
    public void Savepoint(string action,string name){var sql=SavepointSql(sqlServer,action,name);if(sql.Length>0)Execute(sql);}
    public string Limit(int count) => sqlServer ? $" OFFSET 0 ROWS FETCH NEXT {count} ROWS ONLY" : $" LIMIT {count}";
    public string Page => sqlServer ? " OFFSET $offset ROWS FETCH NEXT $limit ROWS ONLY" : " LIMIT $limit OFFSET $offset";
    public DbCommand Command(string sql, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandTimeout = 30;
        // Parameter tokens only; never alter literals, identifiers or user values.
        if (sqlServer) sql = SqlParameters(sql);
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var p = command.CreateParameter(); p.ParameterName = (sqlServer ? "@" : "$") + name;
            p.Value = value ?? DBNull.Value; command.Parameters.Add(p);
        }
        if (SecurityContext.Current is { } access && !parameters.Any(p => p.Name == "accessUser"))
        { var p = command.CreateParameter(); p.ParameterName = sqlServer ? "@accessUser" : "$accessUser"; p.Value = access.User.Id; command.Parameters.Add(p); }
        return command;
    }
    private static string SqlParameters(string sql)
    {
        var text = new System.Text.StringBuilder(); bool quoted = false;
        for (int i = 0; i < sql.Length; i++)
        {
            var c = sql[i];
            if (c == '\'')
            {
                if (quoted && i + 1 < sql.Length && sql[i + 1] == '\'') { text.Append("''"); i++; continue; }
                if (!quoted && (i == 0 || sql[i-1] is not ('N' or 'n'))) text.Append('N');
                quoted = !quoted;
            }
            text.Append(c == '$' && !quoted ? '@' : c);
        }
        return text.ToString();
    }
    public int Execute(string sql, params (string Name, object? Value)[] parameters)
    { using var command = Command(sql, parameters); return command.ExecuteNonQuery(); }
    public T Scalar<T>(string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(sql, parameters); var value = command.ExecuteScalar();
        return value is null or DBNull ? default! : (T)Convert.ChangeType(value, typeof(T), CultureInfo.InvariantCulture);
    }
    public List<T> Query<T>(string sql, Func<DbRow, T> map, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(sql, parameters); using var reader = command.ExecuteReader();
        var list = new List<T>(); var row = new DbRow(reader); while (reader.Read()) list.Add(map(row)); return list;
    }
    // Call only in CommerceDatabase.Write: writers are serialized across API instances.
    public void Upsert(string update, string insert, params (string Name, object? Value)[] parameters)
    { if (Execute(update, parameters) == 0) Execute(insert, parameters); }
    public long InsertIdentity(string insert, params (string Name, object? Value)[] parameters) =>
        Scalar<long>(insert + (sqlServer ? "; SELECT CAST(SCOPE_IDENTITY() AS BIGINT);" : "; SELECT last_insert_rowid();"), parameters);
}
