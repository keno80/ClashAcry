using System.Net;
using System.Net.Sockets;
using System.Text;
using ClashAcry.Core;

namespace ClashAcry.Core.Tests;

/// <summary>
/// 取数层的两条下载纪律，全部来自 Task 11 真机端到端（22.5 MB 的包在本机以 ~42 KB/s 跑了 538 s）：
/// ① 静默预算而不是总时长预算——ResponseHeadersRead 之下 HttpClient.Timeout 只管到响应头，正文既不归它管
///    就不该用固定总时长去兜（慢 link 上那等于永久失败），要兜的是"彻底断流"；
/// ② 分母不能是猜的——源站不给可信 Content-Length 时退到 release 元数据的 size；
/// ③ 资产 URL 是远程数据，发请求前先过准入判据（https + GitHub 资产宿主），拒在动网络与磁盘之前。
/// 这里不用 WireMock：它做不到"发半截正文就卡住"，而卡住恰恰是要测的那条路径，所以手搓一个只回头的假源站。
/// </summary>
public class ReleaseClientTests : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly string _dir = Directory.CreateTempSubdirectory("ca-release-").FullName;

    /// <summary>发完正文到断开之间的宽限期，理由见 <see cref="StartSource"/> 的第二段说明。</summary>
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(1);

    /// <summary>
    /// 起一个一次性 HTTP 源站：先回响应头，再按 <paramref name="chunkSizes"/> 一块块吐正文。
    /// 正文吐完后保持连接 <paramref name="silentAfter"/> 再断开。
    /// silentAfter 在这里有第二个身份，别把它调成 0：本夹具**不读请求**，请求字节因此压在接收队列里，
    /// 此时立刻 Close 会让 Windows 发 RST、把还没被客户端读走的响应一起丢掉（实测：正文完整写出、
    /// 客户端却在 9 ms 内拿到连接重置）。留一段宽限期等客户端读完，才是"源站发完就断"的真实形态。
    /// </summary>
    private static (string Url, Task Serve) StartSource(int declaredTotal, int[] chunkSizes,
        TimeSpan gap, TimeSpan silentAfter, bool declareLength = true)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var url = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/pkg.zip";

        var serve = Task.Run(async () =>
        {
            using var sock = await listener.AcceptTcpClientAsync();
            listener.Stop();
            sock.NoDelay = true;                      // 正文的分块节奏就是被测对象，不能被 Nagle 合并掉
            using var s = sock.GetStream();
            // 请求一个字节都不读：GET 没有请求体，把响应直接写回去就够了——而 ReadAsync 单发一次
            // 拿到的长度不可信（CA2022），为了"吃掉请求"去写个读循环不值当。
            // 未知长度的真实形态是分块传输（代理/CDN 抹掉声明、上游用 chunked 都落在这条上）；
            // 裸的"既不声明也不分块、以 EOF 收尾"会被 .NET 判成协议违规，那不是产品会遇到的东西。
            var head = declareLength
                ? $"HTTP/1.1 200 OK\r\nContent-Length: {declaredTotal}\r\nConnection: close\r\n\r\n"
                : "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n";
            await s.WriteAsync(Encoding.ASCII.GetBytes(head));

            var payload = new byte[4096];
            foreach (var size in chunkSizes)
            {
                if (!declareLength) await s.WriteAsync(Encoding.ASCII.GetBytes($"{size:x}\r\n"));
                await s.WriteAsync(payload.AsMemory(0, size));
                if (!declareLength) await s.WriteAsync(Encoding.ASCII.GetBytes("\r\n"));
                await s.FlushAsync();
                await Task.Delay(gap);
            }
            if (!declareLength) await s.WriteAsync(Encoding.ASCII.GetBytes("0\r\n\r\n"));
            await Task.Delay(silentAfter);                // 声明值没读满就晾着这条连接
        });
        return (url, serve);
    }

    /// <summary>
    /// 生产的准入判据是「https + GitHub 资产宿主」，本夹具的假源站在 <c>http://127.0.0.1</c> 上，
    /// 按默认判据必拒（<see cref="TryDownload_RejectsUrlThatIsNotAnHttpsGitHubAsset"/> 钉的就是这条）。
    /// 所以下载编排的用例一律显式注入"只放回环"这道缝——不是关掉校验，是把校验的对象换成本夹具的源站。
    /// </summary>
    private static bool LoopbackAssetUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Host == "127.0.0.1";

    [Fact]
    public async Task TryDownload_BodyStalls_FailsWithinInactivityBudget_AndLeavesNoPartial()
    {
        var target = Path.Combine(_dir, "pkg.zip");
        // 声明 1 MB、只发 4 KB 然后静默 10 s；预算 400 ms → 断流必须在预算内被放弃。
        var (url, _) = StartSource(1_000_000, [4096], TimeSpan.Zero, TimeSpan.FromSeconds(10));
        var client = new ReleaseClient(_http, inactivityBudget: TimeSpan.FromMilliseconds(400),
            downloadGuard: LoopbackAssetUrl);

        var started = Environment.TickCount64;
        var ok = await client.TryDownloadAsync(url, target, null, default);
        var elapsed = Environment.TickCount64 - started;

        Assert.False(ok);
        Assert.InRange(elapsed, 0, 5_000);                 // 死在预算附近，而不是源站的 10 s 静默或 HTTP 超时
        Assert.False(File.Exists(target));
        Assert.False(File.Exists(target + ".partial"));   // 半截包不能留在缓存目录里当"命中"
    }

    [Fact]
    public async Task TryDownload_SlowButAlive_OutlastsTheBudgetAndStillCompletes()
    {
        var target = Path.Combine(_dir, "pkg.zip");
        // 12 块 × 100 ms = 总时长 ~1.2 s，远超 500 ms 预算；但每块间隔没超预算，所以必须完整落盘。
        // 这条就是"静默预算 vs 总时长预算"的分界：换成总时长上限，慢 link 上的用户永远更新不成功。
        var (url, serve) = StartSource(12 * 4096,
            Enumerable.Repeat(4096, 12).ToArray(), TimeSpan.FromMilliseconds(100), Grace);
        var client = new ReleaseClient(_http, inactivityBudget: TimeSpan.FromMilliseconds(500),
            downloadGuard: LoopbackAssetUrl);

        Assert.True(await client.TryDownloadAsync(url, target, null, default));
        Assert.Equal(12 * 4096, new FileInfo(target).Length);
        await serve;
    }

    [Fact]
    public async Task TryDownload_NoDeclaredLength_FallsBackToAssetSizeHint()
    {
        var target = Path.Combine(_dir, "pkg.zip");
        var (url, serve) = StartSource(0, [4096, 4096, 4096], TimeSpan.Zero, Grace, declareLength: false);
        const long hint = 22_543_546;                     // 真 GitHub 上 v1.19.33 compatible 包的实际 size（spec §4.5）

        var seen = new List<(long Received, long Total)>();
        Assert.True(await new ReleaseClient(_http, downloadGuard: LoopbackAssetUrl).TryDownloadAsync(url, target,
            (r, t) => seen.Add((r, t)), default, hint));

        Assert.NotEmpty(seen);
        Assert.All(seen, s => Assert.Equal(hint, s.Total));   // 分母自始至终是元数据 size，不是猜出来的实际值
        Assert.Equal(3 * 4096, seen.Last().Received);
        Assert.True(seen.Zip(seen.Skip(1), (a, b) => b.Received >= a.Received).All(x => x), "已收字节倒退了");
        await serve;
    }

    [Fact]
    public async Task TryDownload_RejectsUrlThatIsNotAnHttpsGitHubAsset()
    {
        // 资产 URL 来自 release 元数据 = 远程可控。默认判据下这三种形态都必须当场拒绝、且**不发请求**：
        // 明文、别家主机、以及"以 github.com 结尾"的伪装机。这里不起源站——起了也没人连，
        // 那正好是判据：地址没过闸，连一条 TCP 都不该建立。
        var client = new ReleaseClient(_http);          // 不注入 downloadGuard：走生产判据
        foreach (var url in new[]
        {
            "http://github.com/MetaCubeX/mihomo/releases/download/v9/x.zip",
            "https://github.com.evil.test/x.zip",
            "https://evil.test/github.com/x.zip",
            "not-a-url",
        })
        {
            var target = Path.Combine(_dir, "pkg.zip");
            Assert.False(await client.TryDownloadAsync(url, target, null, default));
            Assert.False(File.Exists(target));
            Assert.False(File.Exists(target + ".partial"));   // 拒在动磁盘之前
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }
}
