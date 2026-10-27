using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClashAcry.Core;

/// <summary>
/// GitHub releases 的资产条目。<see cref="Size"/> 只作展示，下载进度的分母取响应的 Content-Length——
/// 那才是这次真的会读到的字节数（重定向、CDN 压缩都可能让两者不一致）。
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
    /// <summary>GitHub API 无 User-Agent 直接 403（实测约束，spec §4.2）。逐请求带。</summary>
    public const string UserAgent = "ClashAcry";

    public const string DefaultLatestUrl = "https://api.github.com/repos/MetaCubeX/mihomo/releases/latest";

    private readonly HttpClient _http;
    private readonly string _latestUrl;

    public ReleaseClient(HttpClient http, string latestUrl = DefaultLatestUrl)
    {
        _http = http; _latestUrl = latestUrl;
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
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
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
                var buf = new byte[81920];
                long received = 0; int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    received += n;
                    // 块内一律报声明值：这里报 max(declared, received) 会让第一个块就"100%"，进度条全程贴着满格走。
                    onBytes?.Invoke(received, total);
                }
                onBytes?.Invoke(received, Math.Max(total, received));
            }
            File.Move(partial, targetPath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
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
