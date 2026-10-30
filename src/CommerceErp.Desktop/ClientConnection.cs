using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CommerceErp.Core;

namespace CommerceErp.Desktop;

public sealed record ClientConnection(string Mode, string Url, string ProtectedToken, string ProfileId)
{
    public static string ConfigPath => Path.Combine(AppContext.BaseDirectory, "client.json");
    public static ClientConnection Local => new("Local", "", "", "");
    public static ClientConnection Load() => File.Exists(ConfigPath) ? JsonSerializer.Deserialize<ClientConnection>(File.ReadAllText(ConfigPath), ErpJson.Options) ?? throw new InvalidDataException("连接配置为空。") : Local;
    public string Token() => Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(ProtectedToken), null, DataProtectionScope.CurrentUser));
    public static ClientConnection Remote(string url, string token, bool remember = false) => new("Remote", ValidateAddress(url).AbsoluteUri,
        remember ? Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(token), null, DataProtectionScope.CurrentUser)) : "", Guid.NewGuid().ToString("N"));
    public static Uri ValidateAddress(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.UserInfo) || uri.Query.Length>0 || uri.Fragment.Length>0 || uri.AbsolutePath!="/" ||
            (uri.Scheme != "https" && !(uri.Scheme=="http" && uri.IsLoopback)))
            throw new InvalidDataException("请输入服务根地址；远程连接必须使用 HTTPS，本机可使用 HTTP。地址不能包含凭证、路径或参数。");
        return uri;
    }
    public string ProfileDirectory()
    {
        if (!Guid.TryParseExact(ProfileId, "N", out _)) throw new InvalidDataException("连接档案标识无效。");
        return Path.Combine(AppContext.BaseDirectory, "profiles", ProfileId);
    }
    public void Save()
    {
        var temporary = ConfigPath + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        { JsonSerializer.Serialize(stream, this, ErpJson.Options); stream.Flush(true); }
        File.Move(temporary, ConfigPath, true);
    }
}
