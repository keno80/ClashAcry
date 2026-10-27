using ClashAcry.Contracts;

namespace ClashAcry.Core;

/// <summary>
/// 换文件动作的执行者签名（与 <see cref="CoreManager.SwapExecutableAsync"/> 一致）。
/// 更新器**不碰内核进程**（spec §3 禁令 1）：它只交出"怎么换文件"这段回调，
/// 停核、换、起核、状态迁移全在 CoreManager 的生命周期锁里做。
/// </summary>
public delegate Task<SwapOutcome> SwapAsync(Func<Task> replaceFiles, CancellationToken ct);

/// <summary>
/// 内置内核的在线更新：查最新 tag → 选包 → 下载/复用缓存 → 解 exe → 交给 swap 换文件 → 记 version.txt。
/// 三条不变量：
/// ① 任何一步失败，旧 exe 一字未动（除 <c>.bak</c> 备份外不写目标），失败返回对应 <see cref="UpdateResult"/>，不抛；
/// ② <c>version.txt</c> 只在文件真的换上后写——提前写会让下次检查误判"已是最新"，用户永远卡在坏版本；
/// ③ 已最新时零联网、零换文件。
/// </summary>
public sealed class CoreUpdater
{
    private readonly AppPaths _paths;
    private readonly ReleaseClient _client;

    public CoreUpdater(AppPaths paths, ReleaseClient client)
    {
        _paths = paths; _client = client;
        _paths.EnsureCreated();
    }

    /// <summary>最新 tag，取不到返回 null（静默降级：UI 只是不显示"有新版"）。</summary>
    public async Task<string?> CheckLatestAsync(CancellationToken ct) =>
        (await _client.GetLatestAsync(ct))?.TagName;

    /// <summary>
    /// 按 tag 精确后缀选包：先 compatible 再 plain amd64。
    /// 用整名相等而不是 StartsWith——否则 <c>mihomo-windows-amd64-v1-go120-{tag}.zip</c> 这类工具链变体会被误选。
    /// </summary>
    public static ReleaseAsset? PickAsset(Release release)
    {
        var tag = release.TagName;
        return Find(release, $"mihomo-windows-amd64-compatible-{tag}.zip")
            ?? Find(release, $"mihomo-windows-amd64-{tag}.zip");

        static ReleaseAsset? Find(Release r, string name) =>
            r.Assets.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<UpdateResult> InstallLatestAsync(IProgress<UpdateProgress>? progress,
        SwapAsync swap, CancellationToken ct)
    {
        var release = await _client.GetLatestAsync(ct);
        // 源站不可达与"没有适配当前平台的包"都归 DownloadFailed：枚举里没有第六种结果，
        // 而对 UI 来说这两条的用户可见语义相同——没拿到新内核，旧内核照常跑。
        // （取消不在这儿兜：取元数据那一步的取消被 ReleaseClient 折成 null，下载/换文件阶段的取消照原样抛给调用方。）
        if (release is null) return UpdateResult.DownloadFailed;
        var asset = PickAsset(release);
        if (asset is null) return UpdateResult.DownloadFailed;

        if (string.Equals(CurrentVersion(), release.TagName, StringComparison.OrdinalIgnoreCase))
            return UpdateResult.AlreadyLatest;

        var cacheZip = Path.Combine(_paths.CoreDownloadsDir, asset.Name);
        // 缓存命中的判据是"存在**且**能解出 exe"，不是"文件在就行"——半截包不能把更新永久卡死。
        if (!(File.Exists(cacheZip) && ZipExtractor.HasExecutable(cacheZip)))
        {
            int lastPct = -1;   // 每次安装各有一份节流档位（进度语义 = 下载进度，spec §9）
            var ok = await _client.TryDownloadAsync(asset.BrowserDownloadUrl, cacheZip,
                (received, declared) =>
                {
                    // 声明总长未知（分块传输 / 代理抹掉 Content-Length）就没有百分比可算；末次收尾回调会把
                    // total 归一到实际字节数，所以"完成"这一档永远能报出来。
                    if (declared <= 0) return;
                    var pct = (int)Math.Min(100, received * 100 / declared);
                    // Total 取 max(声明, 实际)：源站的 Content-Length 只是声明，进度条不能出现 received > total（>100%）。
                    // 同一个档位只发一次：UI 侧既要"有进行中反馈"（spec §9）又不能收一屏刷屏事件。
                    if (pct != lastPct) { progress?.Report(new UpdateProgress(received, Math.Max(declared, received), pct)); lastPct = pct; }
                }, ct, asset.Size);
            if (!ok) return UpdateResult.DownloadFailed;   // 传输失败；包能下下来却解不出 exe 归 ExtractFailed，两种失败不混
        }

        // 解到临时文件：换文件动作只做两次 Copy，任何一步炸了旧 exe 都还在原处。
        var staging = Path.Combine(_paths.CoreDir, $".incoming-{Guid.NewGuid():N}.exe");
        try
        {
            if (!ZipExtractor.TryExtractFirstExe(cacheZip, staging)) return UpdateResult.ExtractFailed;

            SwapOutcome outcome;
            try
            {
                outcome = await swap(async () =>
                {
                    // .bak 必须先落：目标被运行中的内核占用时（OS 以 FILE_SHARE_READ 映射镜像，读得动、覆盖不掉），
                    // 第二次 Copy 抛 IOException，此时回滚要有据可依。File.Replace 在这种场景直接失败且不产生备份。
                    File.Copy(_paths.CoreExecutable, _paths.CoreExecutable + ".bak", overwrite: true);
                    File.Copy(staging, _paths.CoreExecutable, overwrite: true);
                }, ct);
            }
            // swap 的执行方若没把 IOException 折成 Rollback（真 CoreManager 会折），这里按同一语义归类。
            catch (IOException) { return UpdateResult.InUseRolledBack; }

            if (outcome.RolledBack) return UpdateResult.InUseRolledBack;

            // Done(false) 也走这条：包确实换上了，只是内核没能重新拉起。
            // 进程语义不由更新器发明结果，由 CoreManager 的 core.state（+ reason）广播表达（裁定 R-11h）。
            File.WriteAllText(_paths.CoreVersionFile, release.TagName);
            return UpdateResult.Updated;
        }
        finally { try { File.Delete(staging); } catch (IOException) { } }
    }

    private string? CurrentVersion() =>
        File.Exists(_paths.CoreVersionFile) ? File.ReadAllText(_paths.CoreVersionFile).Trim() : null;
}
