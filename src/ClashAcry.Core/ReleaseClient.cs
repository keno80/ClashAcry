using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClashAcry.Core;

/// <summary>
/// GitHub releases 的资产条目。<see cref="Size"/> 是下载进度分母的**候补**：优先用响应头的 Content-Length
/// （那才是这次真的会读到的字节数），只在它缺失或不可信时退到元数据 size（实测：WireMock 的假响应会声明一个
/// 与实发字节数无关的值，真 GitHub 则两者一致）。
/// </summary>
public sealed record ReleaseAsset([property: JsonPropertyName("name")] string Name,
                                 [property: JsonPropertyName("browser_download_url")] string BrowserDownloadUrl,
                                 [property: JsonPropertyName("size")] long Size);

public sealed record Release([property: JsonPropertyName("tag_name")] string TagName,
                             [property: JsonPropertyName("assets")] IReadOnlyList<ReleaseAsset> Assets);

/// <summary>
/// 更新源取数：release 元数据 + 资产下载。
/// 两条纪律：① 失败一律静默（返回 null / false），调用方拿"取不到"当降级，不当故障——检查新版是后台动作，
/// 断网时不该在托盘里炸出一条异常；② <see cref="HttpClient"/> 由外部注入且与订阅下载共用实例，
/// 所以 User-Agent **只在请求消息上带**，绝不写 DefaultRequestHeaders（同 Task 6 的 R-6a 结论）。
/// </summary>
public sealed class ReleaseClient
{
    /// <summary>
    /// 逐请求带的 User-Agent。计划原本写的是"GitHub API 无 UA 直接 403"——Task 11 实测**否证**了这条
    /// （<c>curl -H "User-Agent;"</c> 仍回 200）；保留 UA 的理由换成实的：GitHub 的 API 文档要求客户端自报标识，
    /// 现在的"不拦"是宽容不是契约，而中间的代理/企业网关确实会按 UA 拦无名请求。带它零成本。
    /// </summary>
    public const string UserAgent = "ClashAcry";

    public const string DefaultLatestUrl = "https://api.github.com/repos/MetaCubeX/mihomo/releases/latest";

    /// <summary>正文读取的**静默**预算：连续这么久没收到任何一块就放弃（不是总时长上限，理由见 TryDownloadAsync）。</summary>
    public static readonly TimeSpan InactivityBudget = TimeSpan.FromSeconds(30);

    private readonly HttpClient _http;
    private readonly string _latestUrl;
    private readonly TimeSpan _inactivityBudget;

    /// <param name="inactivityBudget">只给测试留的缝：默认的 30 s 静默预算在真链路上是对的判据，
    /// 但一条断流连接不能被测成 30 s 的套件时长。生产侧一律不传。</param>
    public ReleaseClient(HttpClient http, string latestUrl = DefaultLatestUrl, TimeSpan? inactivityBudget = null)
    {
        _http = http; _latestUrl = latestUrl; _inactivityBudget = inactivityBudget ?? InactivityBudget;
    }

    /// <summary>取最新 release。非 2xx / 超时 / 断网 / JSON 形状不对 → null。</summary>
    public async Task<Release?> GetLatestAsync(CancellationToken ct)
    {
        try
        {
            using var resp = await SendAsync(_latestUrl, ct);
            if (!resp.IsSuccessStatusCode) return null;
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            return JsonSerializer.Deserialize<Release>(stream);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException
                                   or JsonException or InvalidOperationException)
        { return null; }
    }

    /// <summary>
    /// 流式下载到 <paramref name="targetPath"/>：先写 <c>.partial</c>，读满才 <see cref="File.Move"/> 转正。
    /// 半截包永远留在 <c>.partial</c>，"缓存命中"的判据（文件在且能解出 exe）才可靠。
    /// </summary>
    /// <param name="onBytes">每块落盘后回调 (已收字节, 总字节)。总字节未知时传 0，由调用方决定怎么表达"进度未知"；
    /// 读完后再补一次 (received, max(总字节, received))，让"收尾 100%"在总长未知时也有落点。</param>
    /// <param name="totalHint">响应没给可信 Content-Length 时（分块传输 / 代理抹头）用作分母的资产大小，取 release 元数据的 size。</param>
    /// <returns>成功转正为 true；任何失败为 false 且不留下 target。</returns>
    public async Task<bool> TryDownloadAsync(string url, string targetPath,
        Action<long, long>? onBytes, CancellationToken ct, long totalHint = 0)
    {
        var partial = targetPath + ".partial";
        try
        {
            using var resp = await SendAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return false;
            // 实测（Task 11）：WireMock 的 WithBody(byte[]) 会声明一个与实发字节数无关的 Content-Length
            // （2,000,767 字节的包声明 3987），带 contentType 的重载则干脆不发这个头。
            // 所以声明值只在"看着可信"（>0）时用，缺失就落到元数据 size——进度条的分母不能是猜的。
            var declared = resp.Content.Headers.ContentLength ?? 0;
            var total = declared > 0 ? declared : totalHint;

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            await using (var src = await resp.Content.ReadAsStreamAsync(ct))
            await using (var dst = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                // 实测：`HttpCompletionOption.ResponseHeadersRead` 之下，HttpClient.Timeout 只管到**响应头**，
                // 正文读取完全不受它约束（真机 22.5 MB 的包在 ~5 min 预算外跑了 9 min 也没被掐）。
                // 所以这里给的必须是**静默预算**而不是总时长预算：一条彻底断流的 CDN 连接要能在 30 s 内被放弃，
                // 而一条慢但活着（实测 1.9 MB/min）的连接不能被误杀——固定总时长在后者上是永久失败。
                using var idle = new CancellationTokenSource();
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, idle.Token);
                var buf = new byte[81920];
                long received = 0; int n;
                idle.CancelAfter(_inactivityBudget);
                while ((n = await src.ReadAsync(buf, linked.Token)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), linked.Token);
                    received += n;
                    idle.CancelAfter(_inactivityBudget);   // 每来一块就重新上表
                    // 块内一律报声明值：这里报 max(declared, received) 会让第一个块就"100%"，进度条全程贴着满格走。
                    onBytes?.Invoke(received, total);
                }
                onBytes?.Invoke(received, Math.Max(total, received));
            }
            File.Move(partial, targetPath, overwrite: true);
            return true;
        }
        // OperationCanceledException 而不是 TaskCanceledException：正文读取被 idle.Token 掐掉时抛的是基类，
        // 按子类抓会让"静默超时"这条新加的护栏变成未捕获异常穿透到调用方。
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException
                                   or IOException or UnauthorizedAccessException or NotSupportedException)
        { return false; }
        finally
        {
            // 失败的 .partial 不留在缓存目录里：否则下次"缓存命中"判据面对的是一个永远解不开的壳。
            try { if (File.Exists(partial)) File.Delete(partial); } catch (IOException) { }
        }
    }

    private Task<HttpResponseMessage> SendAsync(string url, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.ParseAdd(UserAgent);
        return _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
    }
}
