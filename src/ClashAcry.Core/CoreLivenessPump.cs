using ClashAcry.Contracts;
namespace ClashAcry.Core;

/// <summary>
/// 裁定 R-23 的第二条通路：<see cref="CoreManager"/> 进了 `Running` 之后，进程的死活原本只剩
/// 启动窗口里那圈 `HasExited` 兜着——真机后果（spec §12 第六轮实测）是用户在任务管理器里结束 mihomo
/// 之后，界面还写「内核运行中」，`/version` 早已无人应答。
/// 本类**只管节奏，不管判据**：判据一律是 <see cref="CoreManager.CheckLivenessAsync"/> 的 §9 双探活，
/// 落点一律是 <see cref="CoreManager.MarkCrashedByLivenessAsync"/>。
///
/// 参数（2026-10-28 用户批准）：默认每 5 s 一轮、**只在 `Running` 探**、**连续 2 轮**不符才判死，
/// 最坏 ~10 s 才说实话。单轮不判死是有意的：真机上一轮探不到太容易是毛刺（GC、正在下 geo 库、
/// controller 瞬时无答），把还在跑的内核说成崩了会让用户平白丢一张界面。
/// 判死之后**不自动重启**（spec §9「可预期的显式启停」），落 `Crashed` 就停住等人点。
/// </summary>
public sealed class CoreLivenessPump(CoreManager core, TimeSpan? interval = null, int deadRounds = 2)
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(5);

    private readonly TimeSpan _interval = interval ?? DefaultInterval;

    /// <summary>
    /// 常驻任务，照 <c>PipeServer</c>/<c>PumpTooltip</c> 的用法由宿主（TrayHost）起来、随应用的取消令牌停。
    /// 返回 void 是有意的：循环的两种死法（取消、一轮探针炸了）都在内部收干净，没有可观察的结果，
    /// 而 Task 交出去只会让调用方多一个 CS4014 要按着处理。
    /// </summary>
    public void Start(CancellationToken ct) => _ = PumpAsync(ct);

    private async Task PumpAsync(CancellationToken ct)
    {
        var streak = 0;
        try
        {
            while (true)
            {
                await Task.Delay(_interval, ct);
                try
                {
                    if (core.State != CoreState.Running) { streak = 0; continue; }
                    streak = await core.CheckLivenessAsync(ct) ? 0 : streak + 1;
                    if (streak < deadRounds) continue;
                    streak = 0;
                    await core.MarkCrashedByLivenessAsync(deadRounds, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception)
                {
                    // 一轮探针炸了既不算「活」也不算「死」：清账下一轮再说。
                    // 泵的异常绝不外抛——R-10j② 的教训反着用：为一条低频检查把 Tray 宿主拖死更糟。
                    streak = 0;
                }
            }
        }
        catch (OperationCanceledException) { /* 宿主在退出，泵跟着停，不外抛 */ }
    }
}
