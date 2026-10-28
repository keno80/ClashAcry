using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
namespace ClashAcry.Core;

public sealed class MihomoClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http; private readonly Uri _base; private readonly string _secret;
    public MihomoClient(HttpClient http, string host, int port, string secret)
    {
        _http = http; _secret = secret;
        _base = new Uri($"http://{host}:{port}");
        // 裁定 R-6a：绝不写 _http.DefaultRequestHeaders——这个 HttpClient 在 Tray 里还被订阅下载
        // 与 GitHub API 复用，写进去等于把 controller secret 发给别人的服务器。鉴权头只挂在 msg 上。
    }

    public Task<VersionInfo?> GetVersionAsync(CancellationToken ct) =>
        GetAsync<VersionInfo?>("/version", ct);
    public Task<ProxiesResponse> GetProxiesAsync(CancellationToken ct) =>
        GetAsync<ProxiesResponse>("/proxies", ct);
    public Task<CoreConfigView?> GetConfigsAsync(CancellationToken ct) =>
        GetAsync<CoreConfigView?>("/configs", ct);
    public Task SetProxyAsync(string group, string node, CancellationToken ct) =>
        SendAsync(HttpMethod.Put, $"/proxies/{Uri.EscapeDataString(group)}", new { name = node }, ct);
    public Task SetModeAsync(string mode, CancellationToken ct) =>
        SendAsync(HttpMethod.Patch, "/configs", new { mode }, ct);
    public Task ReloadConfigAsync(string path, CancellationToken ct) =>
        SendAsync(HttpMethod.Patch, "/configs", new { path }, ct);
    public Task RefreshProviderAsync(string name, CancellationToken ct) =>
        SendAsync(HttpMethod.Put, $"/providers/proxies/{Uri.EscapeDataString(name)}", null, ct);
    public Task<ProvidersResponse> GetProvidersAsync(CancellationToken ct) =>
        GetAsync<ProvidersResponse>("/providers/proxies", ct);

    public async Task<int> GetDelayAsync(string name, string url, int timeoutMs, CancellationToken ct)
    {
        var path = $"/proxies/{Uri.EscapeDataString(name)}/delay?url={Uri.EscapeDataString(url)}&timeout={timeoutMs}";
        try { var d = await GetAsync<JsonElement>(path, ct); return d.GetProperty("delay").GetInt32(); }
        // 只把「测不了」（409 / 超时）折成 -1。Task 1 实测 provider 成员名与 provider 名一律 **404**
        // （spec §4.2 已写成既定实现要求；第六轮 2×2 复证：file/http 车型、节点可达与否都改不了这条，
        // 404 的唯一判据是"这个名字不在 GET /proxies 顶层表里"——内联订阅的节点在表里，所以产品路径出得来值）：
        // 404 = 调用方用错了通路，必须原样抛出去，不许在这里顺手吞成 -1，
        // 否则 Task 15 误调成员时会安静地显示"超时"，永远看不出是通路用错。
        catch (ApiException e) when (e.StatusCode is 409) { return -1; }
        // 裁定 R-6b：`when (!ct.IsCancellationRequested)` 把「调用方取消」留在异常通道里。
        // HttpClient.Timeout 到点也抛 TaskCanceledException，但那时 ct 没被请求过，仍折成 -1。
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return -1; }
    }

    private async Task<T> GetAsync<T>(string path, CancellationToken ct) =>
        Parse<T>(await Send(HttpMethod.Get, path, null, ct));
    private Task SendAsync(HttpMethod m, string path, object? body, CancellationToken ct) => Send(m, path, body, ct);

    private async Task<string> Send(HttpMethod m, string path, object? body, CancellationToken ct)
    {
        using var msg = new HttpRequestMessage(m, new Uri(_base, path));
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _secret);   // 每请求一次，不落共享状态
        if (body is not null) msg.Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json");
        using var res = await _http.SendAsync(msg, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode) throw new ApiException((int)res.StatusCode, text);
        return text;
    }
    // T 为可空类型（VersionInfo? / CoreConfigView?）时空体返回 null；非空类型由调用方自行判 null
    private static T Parse<T>(string text) =>
        string.IsNullOrEmpty(text) ? default! : JsonSerializer.Deserialize<T>(text, Json) ?? default!;
}

public sealed class ApiException : Exception
{
    public int StatusCode { get; } public string Body { get; }
    public ApiException(int code, string body) : base($"mihomo API {code}: {body}")
        => (StatusCode, Body) = (code, body);
}
