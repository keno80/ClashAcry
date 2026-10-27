using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using ClashAcry.Contracts;
using ClashAcry.Core;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace ClashAcry.Core.Tests;

/// <summary>
/// 更新器的夹具级要点（都来自派发前裁定）：
/// ① R-11a——进度用自写的同步记录器，`new Progress&lt;T&gt;` 是异步投递，await 返回时可能一条都没跑；
/// ② R-11b/R-11i——swap 一律用 <see cref="SwapSpy"/>，「没换文件」这件事得有次数可断；
/// ③ R-11c——条目内容是不可压缩随机字节，否则 1 MB 全零压成 ~1 KB，节流根本进不了测试；
/// ④ R-11d——占用内核用 FileShare.Read（能读不能写），这才是跑着的 mihomo 的真形态。
/// </summary>
public class CoreUpdaterTests : IDisposable
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly WireMockServer _wire = WireMockServer.Start();
    private readonly string _dir = Directory.CreateTempSubdirectory("ca-updater-").FullName;

    public CoreUpdaterTests() => _wire.Reset();

    private AppPaths Paths()
    {
        var p = AppPaths.ForTest(_dir);
        p.EnsureCreated();
        // 更新器是「覆盖」语义：没有一份可覆盖的旧 exe，「旧内核没被动」这类断言就没有意义。
        if (!File.Exists(p.CoreExecutable)) File.WriteAllBytes(p.CoreExecutable, new byte[] { 0x4d, 0x5a, 1 });
        return p;
    }

    private CoreUpdater Updater() => new(Paths(), new ReleaseClient(Http, $"{_wire.Url}/latest"));
    private string CachedZipPath(AppPaths p) => Path.Combine(p.CoreDownloadsDir, "mihomo-windows-amd64-v9.zip");

    private sealed class SwapSpy
    {
        public int Calls;
        public SwapOutcome Outcome { get; set; } = SwapOutcome.Done(true);
        private async Task<SwapOutcome> Impl(Func<Task> replaceFiles, CancellationToken ct)
        {
            // 与 CoreManager.SwapExecutableAsync 同语义（IOException → 已回滚），但不真起进程。
            Calls++;
            try { await replaceFiles(); return Outcome; }
            catch (IOException) { return SwapOutcome.Rollback(false); }
        }
        public static implicit operator SwapAsync(SwapSpy s) => s.Impl;
    }

    /// <summary>R-11a：同步投递的进度记录器（`Progress&lt;T&gt;` 会把回调甩到线程池，断言会竞态）。</summary>
    private sealed class InlineProgress : IProgress<UpdateProgress>
    {
        public List<UpdateProgress> Reports { get; } = new();
        public void Report(UpdateProgress p) => Reports.Add(p);
        public List<int> Pcts => Reports.Select(r => r.Pct).ToList();
    }

    private static byte[] ZipWithEntry(string entryName, byte[] content)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        using (var e = zip.CreateEntry(entryName).Open()) e.Write(content);
        return ms.ToArray();
    }

    private static byte[] Incompressible(int bytes)
    {
        var buf = new byte[bytes];
        RandomNumberGenerator.Fill(buf);
        return buf;
    }

    // zip == null → 不注册 /dl 映射，下载端点必然 404（模拟源站失败）
    private void StubRelease(byte[]? zip)
    {
        _wire.Given(Request.Create().WithPath("/latest").UsingGet()).RespondWith(
            Response.Create().WithBody(JsonSerializer.Serialize(new
            {
                tag_name = "v9",
                assets = new[] { new { name = "mihomo-windows-amd64-v9.zip", browser_download_url = $"{_wire.Url}/dl", size = zip?.Length ?? 0 } }
            })));
        if (zip is not null)
        {
            // 实测：带 contentType 的 WithBody(byte[]) 干脆不发 Content-Length（裸的那一重载更糟，
            // 声明值和实发字节数无关：2,000,767 报成 3987）。这里保持"无声明长度"这一形态，
            // 让分母走 release 元数据的 size——它等于本内存的 zip 长度，节流才有档位可测。
            _wire.Given(Request.Create().WithPath("/dl").UsingGet()).RespondWith(
                Response.Create().WithBody(zip, "application/octet-stream"));
        }
    }

    [Fact]
    public void PickAsset_PrefersCompatible_AndIgnoresToolchainVariants()
    {
        var r = new Release("v1.19.33", new[]
        {
            new ReleaseAsset("mihomo-linux-amd64-v1.19.33.zip", "u", 1),
            new ReleaseAsset("mihomo-windows-amd64-v1-go120-v1.19.33.zip", "bad", 1),
            new ReleaseAsset("mihomo-windows-amd64-compatible-v1.19.33.zip", "want", 1),
            new ReleaseAsset("mihomo-windows-amd64-v1.19.33.zip", "also-ok", 1),
        });
        Assert.Equal("mihomo-windows-amd64-compatible-v1.19.33.zip", CoreUpdater.PickAsset(r)!.Name);
    }

    [Fact]
    public void PickAsset_FallsBackToPlainAmd64_WhenCompatibleMissing()
    {
        var r = new Release("v1.19.33", new[]
        {
            new ReleaseAsset("mihomo-windows-amd64-v1-go120-v1.19.33.zip", "bad", 1),
            new ReleaseAsset("mihomo-windows-amd64-v1.19.33.zip", "want", 1),
        });
        Assert.Equal("mihomo-windows-amd64-v1.19.33.zip", CoreUpdater.PickAsset(r)!.Name);
    }

    [Fact]
    public void PickAsset_ReturnsNull_WhenNothingMatches()
    {
        var r = new Release("v1.0.0", new[] { new ReleaseAsset("mihomo-darwin-arm64-v1.0.0.zip", "u", 1) });
        Assert.Null(CoreUpdater.PickAsset(r));
    }

    [Fact]
    public async Task Install_ExtractsFirstExeEntryAndRecordsTag()
    {
        var zip = ZipWithEntry("mihomo-windows-amd64-v9.exe", new byte[] { 0x4d, 0x5a, 2, 3 });
        StubRelease(zip);
        var swap = new SwapSpy();

        var res = await Updater().InstallLatestAsync(null, swap, default);

        Assert.Equal(UpdateResult.Updated, res);
        Assert.Equal("v9", File.ReadAllText(Paths().CoreVersionFile).Trim());
        Assert.Equal(new byte[] { 0x4d, 0x5a, 2, 3 }, File.ReadAllBytes(Paths().CoreExecutable));
        Assert.Equal(1, swap.Calls);
    }

    [Fact]
    public async Task AlreadyLatest_SwapsNothing()
    {
        // R-11b：光断返回值等于什么都没断。三条真判据：一次都没换文件、旧 exe 一字未动、没落缓存包。
        // 而且 /dl 干脆不注册——「已最新」若去联网，这里就是 404 而不是 AlreadyLatest。
        StubRelease(zip: null);
        File.WriteAllText(Paths().CoreVersionFile, "v9");
        var p = Paths();
        var before = File.ReadAllBytes(p.CoreExecutable);
        var swap = new SwapSpy();

        Assert.Equal(UpdateResult.AlreadyLatest, await Updater().InstallLatestAsync(null, swap, default));

        Assert.Equal(0, swap.Calls);
        Assert.Equal(before, File.ReadAllBytes(p.CoreExecutable));
        Assert.Empty(Directory.GetFiles(p.CoreDownloadsDir));
    }

    [Fact]
    public async Task Progress_IsMonotonic_Throttled_AndEndsAt100()
    {
        // R-11c：2 MB 不可压缩随机字节。全零会被压成 ~1 KB，pct 一步跳 100，节流进不了测试。
        // 分母走 release 元数据的 size（WireMock 在这个形态下不发 Content-Length，见 StubRelease 注释）。
        var zip = ZipWithEntry("mihomo-windows-amd64-v9.exe", Incompressible(2_000_000));
        StubRelease(zip);
        var seen = new InlineProgress();

        await Updater().InstallLatestAsync(seen, new SwapSpy(), default);

        var pct = seen.Pcts;
        Assert.Equal(100, pct.Last());
        Assert.True(pct.Zip(pct.Skip(1), (a, b) => b >= a).All(x => x), "进度倒退了");
        Assert.True(pct.Count >= 5, $"进度太稀：{pct.Count} 次回调，档位序列 [{string.Join(";", pct)}]");
        Assert.True(pct.Count < 100, $"节流失效：{pct.Count} 次回调");
        Assert.Equal(pct.Count, pct.Distinct().Count());
        Assert.All(seen.Reports, r => Assert.True(r.Received <= r.Total && r.Total > 0));
    }

    [Fact]
    public async Task DownloadFailure_KeepsOldExe_AndWritesNoCache()
    {
        StubRelease(zip: null);                       // /dl 未注册 → 404
        var p = Paths();
        var before = File.ReadAllBytes(p.CoreExecutable);
        var swap = new SwapSpy();

        Assert.Equal(UpdateResult.DownloadFailed, await Updater().InstallLatestAsync(null, swap, default));

        Assert.Equal(before, File.ReadAllBytes(p.CoreExecutable));   // 失败绝不动旧内核
        Assert.False(File.Exists(CachedZipPath(p)));                  // 半截包不进缓存
        Assert.Equal(0, swap.Calls);
    }

    [Fact]
    public async Task CachedZip_SkipsNetworkEntirely_AndStillInstalls()
    {
        var zip = ZipWithEntry("mihomo-windows-amd64-v9.exe", new byte[] { 4, 5, 6 });
        StubRelease(zip: null);                       // 只有 /latest 可达
        var p = Paths();
        File.WriteAllBytes(CachedZipPath(p), zip);

        Assert.Equal(UpdateResult.Updated, await Updater().InstallLatestAsync(null, new SwapSpy(), default));
        Assert.Equal(new byte[] { 4, 5, 6 }, File.ReadAllBytes(p.CoreExecutable));
    }

    [Fact]
    public async Task CorruptCache_FallsBackToDownload()
    {
        // 缓存命中的判据是「存在**且**能解出 .exe」，不是「文件在就行」——半截/坏包不能把更新永久卡死。
        var p = Paths();
        File.WriteAllBytes(CachedZipPath(p), new byte[] { 1, 2, 3, 4, 5 });
        StubRelease(ZipWithEntry("mihomo-windows-amd64-v9.exe", new byte[] { 8, 8 }));

        Assert.Equal(UpdateResult.Updated, await Updater().InstallLatestAsync(null, new SwapSpy(), default));
        Assert.Equal(new byte[] { 8, 8 }, File.ReadAllBytes(p.CoreExecutable));
    }

    [Fact]
    public async Task InUseTarget_RollsBackAndReportsInUseRolledBack()
    {
        // R-11d：FileShare.Read 而不是 None。跑着的 mihomo 由 OS 以 FILE_SHARE_READ 映射自己的镜像——
        // 读得动（所以 .bak 备份成功）、覆盖不掉（所以换文件抛 IOException）。用 None 的话备份这步就先炸，
        // 「回滚有据」那条断言永远不成立，样例照写必红。
        StubRelease(ZipWithEntry("mihomo-windows-amd64-v9.exe", new byte[] { 9 }));
        var p = Paths();
        using var hold = new FileStream(p.CoreExecutable, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);

        Assert.Equal(UpdateResult.InUseRolledBack, await Updater().InstallLatestAsync(null, new SwapSpy(), default));
        Assert.True(File.Exists(p.CoreExecutable + ".bak"));                  // 替换前先落 .bak，回滚有据
        Assert.False(File.Exists(p.CoreVersionFile));                          // 没换成，version.txt 也不许写：写了下次就以为已是 v9
    }

    [Fact]
    public async Task SwapDoneButCoreNotRestarted_StillReportsUpdated()
    {
        // R-11h：Done(false) = 包换上了但内核没被重新拉起。更新器不发明第六种结果——
        // 进程语义由 CoreManager 的 core.state（Crashed + reason）广播表达，这里只报「换上了」。
        StubRelease(ZipWithEntry("mihomo-windows-amd64-v9.exe", new byte[] { 1, 1 }));
        var swap = new SwapSpy { Outcome = SwapOutcome.Done(false) };

        Assert.Equal(UpdateResult.Updated, await Updater().InstallLatestAsync(null, swap, default));
        Assert.Equal("v9", File.ReadAllText(Paths().CoreVersionFile).Trim());
    }

    [Fact]
    public async Task ZipWithoutExeEntry_ReportsExtractFailed_KeepsOldExe()
    {
        // R-11i：ExtractFailed 此前零覆盖。
        StubRelease(ZipWithEntry("README.txt", new byte[] { 1, 2, 3 }));
        var p = Paths();
        var before = File.ReadAllBytes(p.CoreExecutable);
        var swap = new SwapSpy();

        Assert.Equal(UpdateResult.ExtractFailed, await Updater().InstallLatestAsync(null, swap, default));

        Assert.Equal(before, File.ReadAllBytes(p.CoreExecutable));
        Assert.Equal(0, swap.Calls);
    }

    [Fact]
    public async Task CheckLatest_ReturnsNull_OnUnreachableSource_NotThrow()
    {
        // R-11f 的另一半：拿不到版本信息是静默降级（UI 只是不显示「有新版」），不是异常。
        var p = Paths();
        var dead = new CoreUpdater(p, new ReleaseClient(Http, $"{_wire.Url}/never-stubbed"));
        Assert.Null(await dead.CheckLatestAsync(default));
    }

    public void Dispose()
    {
        _wire.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }   // 别往 %TEMP% 堆垃圾（Task 5 的账）
    }
}
