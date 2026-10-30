using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using CommerceErp.Infrastructure;

namespace CommerceErp.Api;

public sealed class ServerSettings
{
    public string ListenAddress { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 5088;
    public string? CertificateFile { get; set; }
    public string? TokenFile { get; set; }
    public string DatabaseProvider { get; set; } = "Sqlite";
    public string DataDirectory { get; set; } = "data";
    public string? SqlConnectionFile { get; set; }
    [JsonIgnore] public bool Standalone { get; private set; }
    [JsonIgnore] public string Root { get; private set; } = AppContext.BaseDirectory;
    public string Resolve(string path) => Path.GetFullPath(path, Root);
    public static ServerSettings Load(string[] args)
    {
        var index = Array.IndexOf(args, "--server");
        if (index < 0) return new() { Port = 0, DataDirectory = Environment.GetEnvironmentVariable("ERP_DATA_DIR") ?? Path.Combine(AppContext.BaseDirectory,"data") };
        if (index+1 >= args.Length) throw new InvalidDataException("--server 后必须提供配置文件路径。");
        var path = Path.GetFullPath(args[index+1]);
        var settings = JsonSerializer.Deserialize<ServerSettings>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow }) ?? throw new InvalidDataException("服务配置为空。");
        settings.Standalone = true; settings.Root = Path.GetDirectoryName(path)!;
        if (!IPAddress.TryParse(settings.ListenAddress,out var address) || settings.Port is <0 or >65535) throw new InvalidDataException("监听地址必须是 IP，端口必须为 0–65535。");
        if (!IPAddress.IsLoopback(address) && string.IsNullOrWhiteSpace(settings.CertificateFile)) throw new InvalidDataException("非本机监听必须配置受信任的 HTTPS 证书。");
        if (settings.DatabaseProvider is not ("Sqlite" or "SqlServer")) throw new InvalidDataException("数据库类型仅支持 Sqlite / SqlServer。");
        return settings;
    }
    public string ReadToken()
    {
        var token = Standalone && TokenFile is not null ? File.ReadAllText(Resolve(TokenFile)).Trim() : Environment.GetEnvironmentVariable("ERP_SESSION_TOKEN");
        if (token is null || token.Length < 32 || token.Any(char.IsWhiteSpace)) throw new InvalidDataException("服务令牌至少 32 字符且不含空白；请通过环境变量或受保护文件配置。");
        return token;
    }
    public CommerceDatabase OpenDatabase() => DatabaseProvider == "Sqlite" ? new(Resolve(DataDirectory)) :
        CommerceDatabase.SqlServer(SqlConnectionFile is null ? Environment.GetEnvironmentVariable("ERP_SQL_CONNECTION") ?? throw new InvalidDataException("缺少 SQL Server 连接配置。") : File.ReadAllText(Resolve(SqlConnectionFile)).Trim());
}
