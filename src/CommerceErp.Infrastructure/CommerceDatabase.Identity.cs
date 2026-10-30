using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.Data.SqlClient;

namespace CommerceErp.Infrastructure;

public sealed partial class CommerceDatabase
{
    // Recovery preflight must never create a database or apply migrations.
    public static (string InstanceId,int Schema) InspectExisting(string? directory,string? sqlConnection)
    {
        DbConnection connection;
        if(directory is not null)
        {
            _=Provider.Value;
            var path=Path.Combine(Path.GetFullPath(directory),"commerce.db");
            if(!File.Exists(path))throw new FileNotFoundException("指定目录中不存在 commerce.db。");
            connection=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path,Mode=SqliteOpenMode.ReadOnly,Pooling=false}.ToString());
        }
        else connection=new SqlConnection(ValidateSqlConnection(sqlConnection??throw new ArgumentException("请在本机设置 ERP_SQL_CONNECTION。")));
        using(connection)
        {
            connection.Open();using var command=connection.CreateCommand();
            command.CommandText="SELECT MAX(Version) FROM SchemaMigrations";var schema=Convert.ToInt32(command.ExecuteScalar());
            command.CommandText="SELECT Value FROM SystemMetadata WHERE Name='InstanceId'";var identity=Convert.ToString(command.ExecuteScalar())??"";
            if(!Guid.TryParseExact(identity,"N",out _))throw new InvalidDataException("数据库身份无效，未执行恢复。");
            return(identity,schema);
        }
    }
}
