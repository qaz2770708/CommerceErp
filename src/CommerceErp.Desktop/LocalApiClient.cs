using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CommerceErp.Core;

namespace CommerceErp.Desktop;

public sealed class LocalApiClient : IAsyncDisposable
{
    private Process? _process;
    private HttpClient? _http;
    private Task<string>? _stderr;
    private string _pendingPath = "";
    private Dictionary<string, PendingCommand> _pending = [];
    private readonly SemaphoreSlim _commandGate = new(1, 1);
    private FileStream? _clientLease;
    public sealed record PendingCommand(string Path, string Json);
    public int PendingCount => _pending.Count;
    public AccountInfo? CurrentAccount { get; private set; }
    public bool Allows(string permission) => CurrentAccount is not null && AccountPermissions.Allows(CurrentAccount, permission);
    public bool HasAllShops {get;private set;}
    public void SetWorkspace(WorkspaceProfile profile)=>HasAllShops=profile.FullShopScope;
    public async Task<LoginResult> LoginAsync(string username, string password)
    {
        if (CurrentAccount is not null) throw new InvalidOperationException("请先退出当前账号。");
        var login = await PostAsync<LoginResult>("/api/security/login", new LoginRequest(username,password));
        Http.DefaultRequestHeaders.Authorization = new("Bearer", login.Token);
        try
        {
            var path = Path.Combine(Path.GetDirectoryName(_pendingPath)!, "pending-account.txt");
            var owner = File.Exists(path) ? (await File.ReadAllTextAsync(path)).Trim() : null;
            if (_pending.Count > 0 && (owner is not null ? owner != login.Account.Id : login.Account.Role != AccountRole.Administrator))
                throw new InvalidDataException("此连接档案有其他账号的待重试操作，请保留档案并由原账号登录。未绑定的历史记录仅管理员可接管。");
            await File.WriteAllTextAsync(path + ".tmp", login.Account.Id); File.Move(path + ".tmp",path,true);
            CurrentAccount = login.Account; return login;
        }
        catch { try { await PostAsync<JsonElement>("/api/security/logout",new {}); } catch { } Http.DefaultRequestHeaders.Authorization=null; throw; }
    }
    public async Task LogoutAsync()
    {
        try { if(CurrentAccount is not null){using var response=await Http.PostAsJsonAsync("/api/security/logout",new {},ErpJson.Options);if(response.StatusCode!=System.Net.HttpStatusCode.Unauthorized)_=await ReadAsync<JsonElement>(response);} }
        finally { CurrentAccount=null; if(_http is not null)_http.DefaultRequestHeaders.Authorization=null; }
    }
    private void RequireAccount() { if(CurrentAccount is null)throw new InvalidOperationException("请先登录账号。"); }
    public ServiceHealth? Health { get; private set; }
    public string ConnectionLabel => Health is null ? "未连接" : (Health.Mode == "SharedServiceDemo" ? "共享服务" : "本地模式") + " · " + Health.DatabaseProvider;
    public async Task StartConfiguredAsync(ClientConnection configuration, string? sessionToken = null)
    {
        if (configuration.Mode == "Local") await StartAsync();
        else if (configuration.Mode == "Remote") await StartRemoteAsync(configuration.Url, sessionToken ?? configuration.Token(), configuration.ProfileDirectory());
        else throw new InvalidDataException("连接模式无效。");
    }
    public async Task StartRemoteAsync(string url, string token, string profileDirectory)
    {
        var address = ClientConnection.ValidateAddress(url);
        if (token.Length < 32 || token.Any(char.IsWhiteSpace)) throw new InvalidDataException("访问令牌无效。");
        await OpenJournalAsync(profileDirectory);
        _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = address, Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.Add("X-Erp-Session", token);
        await BindIdentityAsync(true);
    }
    private async Task OpenJournalAsync(string dataDirectory)
    {
        if (_clientLease is not null || _http is not null) throw new InvalidOperationException("客户端已经连接，切换服务请重新启动。");
        Directory.CreateDirectory(dataDirectory);
        try { _clientLease = new FileStream(Path.Combine(dataDirectory, "desktop.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new InvalidOperationException("此连接档案已被另一个客户端使用。"); }
        _pendingPath = Path.Combine(dataDirectory, "pending-commands.json");
        if (File.Exists(_pendingPath))
            _pending = JsonSerializer.Deserialize<Dictionary<string, PendingCommand>>(await File.ReadAllTextAsync(_pendingPath), ErpJson.Options) ?? throw new InvalidDataException("待重试记录损坏，请保留文件后检查。");
    }
    public static void ValidateHealth(ServiceHealth health, bool remote)
    {
        if (health.Status != "ok" || health.ProtocolVersion != 7 || health.SchemaVersion != 10 || !health.AccountsRequired || !Guid.TryParseExact(health.InstanceId,"N",out _) ||
            (remote && health.Mode != "SharedServiceDemo")) throw new InvalidDataException("服务器协议、数据库版本或运行模式不兼容。");
    }
    private async Task BindIdentityAsync(bool remote)
    {
        var health = await GetAsync<ServiceHealth>("/api/health"); ValidateHealth(health, remote);
        var identityPath = Path.Combine(Path.GetDirectoryName(_pendingPath)!, "database-identity.txt");
        if (File.Exists(identityPath))
        {
            if ((await File.ReadAllTextAsync(identityPath)).Trim() != health.InstanceId)
                throw new InvalidDataException("数据库身份与连接档案不一致，已阻止操作。保留原档案及待重试记录，为新数据库建立新档案。");
        }
        else
        {
            if (remote && _pending.Count > 0) throw new InvalidDataException("未绑定数据库的历史待重试记录不能发送到共享服务。请保留原档案并建立新档案。");
            await using var file = new FileStream(identityPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,4096,FileOptions.WriteThrough);
            await file.WriteAsync(Encoding.UTF8.GetBytes(health.InstanceId)); file.Flush(true);
        }
        Health = health; Http.DefaultRequestHeaders.Add("X-Erp-Database", health.InstanceId);
    }


    public async Task StartAsync(string? dataDirectory = null)
    {
        dataDirectory ??= Path.Combine(AppContext.BaseDirectory, "data");
        await OpenJournalAsync(dataDirectory);
        var server = FindServer();
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var start = new ProcessStartInfo
        {
            FileName = "dotnet", UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(server)!
        };
        start.ArgumentList.Add(server);
        start.Environment["ERP_SESSION_TOKEN"] = token;
        start.Environment["ERP_PARENT_PROCESS_ID"] = Environment.ProcessId.ToString();
        start.Environment["ERP_DATA_DIR"] = dataDirectory;
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        _process = Process.Start(start) ?? throw new InvalidOperationException("无法启动本地服务。");
        _stderr = _process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        string? line;
        while ((line = await _process.StandardOutput.ReadLineAsync(timeout.Token)) is not null)
        {
            if (!line.StartsWith("ERP_READY|", StringComparison.Ordinal)) continue;
            var address = new Uri(line[10..]);
            if (address.Host != "127.0.0.1" || address.Scheme != "http") throw new InvalidOperationException("本地服务地址不符合预期。");
            _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = address, Timeout = TimeSpan.FromSeconds(20) };
            _http.DefaultRequestHeaders.Add("X-Erp-Session", token);
            await BindIdentityAsync(false);
            return;
        }
        throw new InvalidOperationException("本地服务启动失败。请确认 .NET 10 已安装、数据库与数据目录可用。" +
            (_process.HasExited ? $"（退出码 {_process.ExitCode}）" : ""));
    }

    private static string FindServer()
    {
        var packaged = Path.Combine(AppContext.BaseDirectory, "server", "CommerceErp.Api.dll");
        if (File.Exists(packaged)) return packaged;
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (!File.Exists(Path.Combine(directory.FullName, "CommerceErp.slnx"))) continue;
            foreach (var configuration in new[] { "Debug", "Release" })
            {
                var path = Path.Combine(directory.FullName, "src", "CommerceErp.Api", "bin", configuration, "net10.0", "CommerceErp.Api.dll");
                if (File.Exists(path)) return path;
            }
        }
        throw new FileNotFoundException("未找到后端程序，请先运行 build.ps1 编译完整解决方案。");
    }

    public async Task<T> GetAsync<T>(string path)
    {
        using var response = await Http.GetAsync(path);
        return await ReadAsync<T>(response);
    }
    public async Task<T> PostAsync<T>(string path, object body)
    {
        using var response = await Http.PostAsJsonAsync(path, body, ErpJson.Options);
        return await ReadAsync<T>(response);
    }
    public async Task<T> PutAsync<T>(string path, object body)
    {
        using var response = await Http.PutAsJsonAsync(path, body, ErpJson.Options);
        return await ReadAsync<T>(response);
    }
    // Persist before sending. A timeout or process exit leaves the exact request and key available for retry.
    public async Task<T> PostCommandAsync<T>(string path, object payload)
    {
        RequireAccount();
        await _commandGate.WaitAsync();
        try
        {
            var json = JsonSerializer.Serialize(payload, ErpJson.Options);
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path + "\n" + json)));
            if (!_pending.TryGetValue(key, out var pending))
            {
                var node = JsonNode.Parse(json)!.AsObject(); node["operationId"] = Guid.NewGuid().ToString("N");
                pending = new(path, node.ToJsonString(ErpJson.Options));
                var next = new Dictionary<string, PendingCommand>(_pending) { [key] = pending }; await SavePendingAsync(next);
            }
            return await SendPendingAsync<T>(key, pending);
        }
        finally { _commandGate.Release(); }
    }
    public async Task<int> RetryPendingAsync()
    {
        RequireAccount();
        await _commandGate.WaitAsync();
        try
        {
            var entries = _pending.ToArray();
            foreach (var entry in entries) await SendPendingAsync<JsonElement>(entry.Key, entry.Value);
            return entries.Length;
        }
        finally { _commandGate.Release(); }
    }
    private async Task<T> SendPendingAsync<T>(string key, PendingCommand pending)
    {
        using var body = new StringContent(pending.Json, Encoding.UTF8, "application/json");
        using var response = await Http.PostAsync(pending.Path, body);
        if ((int)response.StatusCode is 400 or 409 or 422)
        { await RemovePendingAsync(key); }
        var result = await ReadAsync<T>(response);
        await RemovePendingAsync(key); return result;
    }
    private Task RemovePendingAsync(string key)
    { var next = new Dictionary<string, PendingCommand>(_pending); next.Remove(key); return SavePendingAsync(next); }
    private async Task SavePendingAsync(Dictionary<string, PendingCommand> next)
    {
        var temporary = _pendingPath + ".tmp";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(next, ErpJson.Options);
        await using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        { await file.WriteAsync(bytes); file.Flush(flushToDisk: true); }
        File.Move(temporary, _pendingPath, overwrite: true);
        _pending = next; // Never forget an uncertain result when updating the local journal fails.
    }
    public async Task<ValidationReport> ValidateAsync(string json)
    {
        // Local parsing gives actionable schema errors before making any HTTP request.
        try { _ = JsonSerializer.Deserialize<SampleOrder>(json, ErpJson.Options) ?? throw new JsonException("订单不能为空。"); }
        catch (JsonException e) { throw new BusinessRuleException($"样例 JSON 不符合契约：{e.Message}"); }
        if (Encoding.UTF8.GetByteCount(json) > 262144) throw new BusinessRuleException("样例内容不能超过 256 KB。");
        using var body = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await Http.PostAsync("/api/validate", body);
        return await ReadAsync<ValidationReport>(response);
    }
    private HttpClient Http => _http ?? throw new InvalidOperationException("服务尚未就绪。");
    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        var json = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            string? message = null;
            try { using var document = JsonDocument.Parse(json); if (document.RootElement.TryGetProperty("message", out var m)) message = m.GetString(); } catch (JsonException) { }
            throw new BusinessRuleException(message ?? $"服务返回 {(int)response.StatusCode}，请检查字段格式后重试。");
        }
        return JsonSerializer.Deserialize<T>(json, ErpJson.Options) ?? throw new InvalidDataException("服务返回空结果。");
    }
    public async ValueTask DisposeAsync()
    {
        try { await LogoutAsync(); } catch { }
        if (_process is { HasExited: false })
        {
            try
            {
                if (_http is not null) await _http.PostAsync("/api/shutdown", null);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await _process.WaitForExitAsync(timeout.Token);
            }
            catch (Exception e) when (e is HttpRequestException or OperationCanceledException or InvalidOperationException)
            { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
        }
        if (_stderr is not null && _process is { HasExited: true }) await _stderr;
        _http?.Dispose(); _process?.Dispose(); _clientLease?.Dispose();
    }
}
