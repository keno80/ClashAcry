using System.Diagnostics;
using ClashAcry.Contracts;
namespace ClashAcry.Core;

public sealed class CoreManager : IAsyncDisposable
{
    private readonly AppPaths _paths; private readonly SettingsStore _store;
    private readonly MihomoClient _client; private readonly CoreProbe _probe;
    private readonly Func<string, ProcessStartInfo> _launch;
    private readonly StderrRing _stderr = new(200);
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly List<CoreStateChanged> _events = new();
    private Process? _proc; private string? _lastReason; private int _disposed;

    public CoreState State { get; private set; } = CoreState.Stopped;
    public int MixedPort { get; private set; }
    // 评审小钉①：这在锁外读 _proc，而 KillProcess 可能并发 _proc.Dispose()（Task 11 的管道线程就读它）
    // → ObjectDisposedException。这里只表达"我们不再知道有活内核"，不许把异常抛给调用方。
    // 复评补强：必须先把 _proc 取到局部变量再用——`_proc is {...} ? _proc.Id` 读了两次字段，
    // 中间那一下可能已经被 KillProcess 置 null（窄窗口 NRE，catch 只兜 ObjectDisposedException 兜不住）。
    public int? RunningProcessId
    {
        get
        {
            var p = _proc;
            if (p is null) return null;
            try { return p.HasExited ? null : p.Id; } catch (ObjectDisposedException) { return null; }
        }
    }
    public event Action<CoreStateChanged>? StateChanged;
    // 评审小钉②：不许把活的 _events 交出去（Transition 还在往里 Add）。同 R-8d 的理由：读侧拷贝 + 加锁。
    public IReadOnlyList<CoreStateChanged> StateChangedEvents { get { lock (_events) return _events.ToArray(); } }
    public int StartAttemptCount { get; private set; }
    public int SpawnCount { get; private set; }
    public int ConcurrentGuardHits { get; private set; }

    public CoreManager(AppPaths paths, SettingsStore store, MihomoClient client, CoreProbe probe,
                       Func<string, ProcessStartInfo>? launchFactory = null)
    {
        (_paths, _store, _client, _probe) = (paths, store, client, probe);
        _launch = launchFactory ?? (exe => DefaultLaunch(paths, exe));
        MixedPort = store.Load().MixedPort;
    }

    /// <summary>
    /// 默认启动参数：`mihomo -d &lt;runtime&gt;`。目录**必须是 RuntimeDir 而不是 Root**——
    /// mihomo 在 -d 里找 `config.yaml`，而注入产物只写在那里（AppPaths.ConfigFile）；
    /// 给成 Root 时那里没有 config.yaml，mihomo 会用空默认配置起来（监听 7890、连 external-controller 都没有），
    /// 双探活于是 controller+mixed 全 fail，界面看到的是「崩溃」而不是「配置没生效」。
    /// Task 8~16 的用例全走注入的假 launchFactory，这条默认路径第一次被真内核跑到时才发现（Task 17 实测）。
    /// </summary>
    public static ProcessStartInfo DefaultLaunch(AppPaths paths, string exe) =>
        new(exe, $"-d \"{paths.RuntimeDir}\"")
        { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };

    public IReadOnlyList<string> StderrTail(int n = 50) => _stderr.Snapshot().TakeLast(n).ToList();

    // 锁纪律：只有公开入口取 _lifecycle，内部 *Locked 方法一律不取（信号量不可重入，Task 11 的换核流程也走这条）
    private async Task<T> InLifecycle<T>(Func<Task<T>> body, CancellationToken ct)
    {
        await _lifecycle.WaitAsync(ct);
        try { return await body(); } finally { _lifecycle.Release(); }
    }

    public Task<bool> StartAsync(CancellationToken ct) => InLifecycle(() => StartLockedAsync(ct), ct);
    // 裁定 R-10b：声明从非泛型 `Task` 改回 `Task<bool>`。实现体本来就 `return true`，只是原声明把结果
    // 洗掉了，于是 Task 10 的 `core.stop` 回包无从表达「真的停了」。Task 8 现有用例都是
    // `await mgr.StopAsync(...)` 不取值，改宽返回类型不破任何断言。
    public Task<bool> StopAsync(CancellationToken ct) => InLifecycle(async () => { await StopLockedAsync(); return true; }, ct);

    /// <summary>
    /// Task 11 用：在生命周期锁内换文件，内核由本方法负责停/起。
    /// 两条真值口径（Task 11 补的三条用例锁住，见 CoreManagerTests 的 Swap_*）：
    /// ① **换之前没在跑就绝不因为换核把它拉起来**——v1 的语义是"可预期的显式启停"，回滚分支若无条件
    ///    StartLockedAsync，一次失败的更新会在用户没点过启动的情况下多出一个内核进程；
    /// ② <see cref="SwapOutcome.CoreRunningAfter"/> 报告的是换完之后**真的**在不在跑，不是"操作成功"。
    /// </summary>
    public Task<SwapOutcome> SwapExecutableAsync(Func<Task> replaceFiles, CancellationToken ct) =>
        InLifecycle(async () =>
        {
            var wasRunning = State is CoreState.Running or CoreState.Starting;
            Transition(CoreState.Updating);
            await StopLockedAsync();
            try { await replaceFiles(); }
            catch (IOException) { var back = wasRunning ? await StartLockedAsync(ct) : false; return SwapOutcome.Rollback(back); }
            var started = wasRunning ? await StartLockedAsync(ct) : false;
            return SwapOutcome.Done(started);
        }, ct);

    private async Task<bool> StartLockedAsync(CancellationToken ct)
    {
        if (State is CoreState.Running or CoreState.Starting) { ConcurrentGuardHits++; return State == CoreState.Running; }

        StartAttemptCount = 0;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            StartAttemptCount = attempt;
            if (await AttemptStart(ct)) return true;
            if (attempt == 1) _lastReason = $"首次启动失败（{_lastReason}），自动重试第 2 次";
        }
        Transition(CoreState.Crashed, _lastReason);
        return false;
    }

    private async Task<bool> AttemptStart(CancellationToken ct)
    {
        Transition(CoreState.Starting, StartAttemptCount > 1 ? _lastReason : null);
        var s = _store.Load();
        MixedPort = s.MixedPort;
        _stderr.Clear();
        // 裁定 R-8e：`_lastReason` 只在首次尝试清。StartLockedAsync 在两次尝试之间写入的
        // 「首次启动失败（…），自动重试第 2 次」必须活过第二次 AttemptStart 的顶部，
        // 否则上面那行 Transition(Starting) 永远带 null，UI 看不出发生过重试。
        if (StartAttemptCount == 1) _lastReason = null;

        var exe = s.CorePath ?? _paths.CoreExecutable;
        if (!File.Exists(exe)) { _lastReason = $"内核文件不存在：{exe}"; return false; }
        // 裁定 R-8a：占用预检走注入的探针实例方法（默认实现是真 TCP 探测），不许在这里裸调
        // CoreProbe.TcpAlive —— 那样夹具里常驻 controller 端口的 WireMock 会把成功路径自己拒掉。
        // 防的是上一个内核孤儿（spec §9）：它占着 9097 时再 spawn 第二个，双探活读的是旧内核的应答。
        if (await _probe.ControllerPortOccupied(s.ControllerHost, s.ControllerPort, ct))
        { _lastReason = $"controller 端口 {s.ControllerPort} 已被其他进程占用"; return false; }

        try
        {
            SpawnCount++;
            _proc = Process.Start(_launch(exe))!;
            // Task 1 实测：mihomo v1.19.33 的 Go 日志全部走 **stdout**，stderr 恒空。
            // 两条流进同一个环（字段名 `_stderr` 是历史名，实际是"内核输出环"），顺序由到达时间决定；
            // 只订阅 ErrorDataReceived 的话日志面板永远是一片空白。每条流各订阅一次，重复订阅会让每行进环两次。
            _proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) _stderr.Add(e.Data); };
            _proc.OutputDataReceived += (_, e) => { if (e.Data is not null) _stderr.Add(e.Data); };
            _proc.BeginOutputReadLine(); _proc.BeginErrorReadLine();

            var controllerOk = false; var mixedOk = false;
            // 裁定 R-8i① / spec §9「探活成本实测」：窗口按**墙上时间**算，不许改回固定轮数当预算。
            // 旧写法 `for (i < 20)` 把 250 ms 当成了预算，可它只是间隔——每轮还要付一次 TCP 探活，
            // 而实测 loopback 上没人听时 connect 要 ~2.03 s 才拿到拒绝，名义 5 s 实际 25 s/尝试（UI 假死）。
            var probeWindow = Stopwatch.StartNew();
            while (probeWindow.Elapsed < TimeSpan.FromSeconds(5))
            {
                await Task.Delay(250, ct);
                if (_proc.HasExited)
                { _lastReason = $"内核进程退出，退出码 {_proc.ExitCode}"; await KillProcess(); return false; }

                // spec §9 状态失真：controller 与 mixed 端口双探活都通过才允许 Running
                controllerOk = await _probe.ControllerAlive(ct);
                mixedOk = controllerOk && await CoreProbe.TcpAlive("127.0.0.1", MixedPort, ct);
                if (mixedOk) { Transition(CoreState.Running); return true; }
            }
            _lastReason = $"探活失败：controller={(controllerOk ? "ok" : "fail")} mixed({MixedPort})={(mixedOk ? "ok" : "fail")}，5s 内未就绪";
            await KillProcess();
            return false;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 裁定 R-8h / spec §9 取消语义：调用方的取消不是「启动失败」，三步顺序不可换——
            // 先杀进程（绝不能留孤儿内核），再 Transition(Stopped)（状态机不许停在没有对应现实的状态里），
            // 最后原样抛（吞掉就等于伪造业务结论）。这条 catch 必须在通用 catch 之前。
            await KillProcess();
            Transition(CoreState.Stopped, "启动被取消");
            throw;
        }
        catch (Exception ex) { _lastReason = ex.Message; await KillProcess(); return false; }
    }

    private async Task StopLockedAsync()
    {
        await KillProcess();
        // 裁定 R-8b（同 Task 7 红线）：只清指向我们这份端口的悬挂代理，
        // 裸调 SystemProxy.Disable() 会把用户此刻正在用的公司/别家代理一起关掉。
        SystemProxy.CleanupIfStale(MixedPort, coreAlive: () => false);
        Transition(CoreState.Stopped);
    }

    public Task ReloadAsync(CancellationToken ct) => _client.ReloadConfigAsync(_paths.ConfigFile, ct);

    private async Task KillProcess()
    {
        if (_proc is null) return;
        try { if (!_proc.HasExited) { _proc.Kill(entireProcessTree: true); await _proc.WaitForExitAsync(); } }
        catch { /* 已被外部杀死 */ }
        finally { _proc?.Dispose(); _proc = null; }   // 置 null → RunningProcessId 随之为 null
    }

    private void Transition(CoreState next, string? reason = null)
    {
        State = next;
        var e = new CoreStateChanged(next, reason, StderrTail());
        lock (_events) _events.Add(e);   // 与 StateChangedEvents 的快照读同一把锁（小钉②／同 R-8d）
        StateChanged?.Invoke(e);   // spec §3 禁令 3：所有迁移点唯一出口，且必须广播
    }

    // 裁定 R-8c：DisposeAsync 也是公开入口，必须和 Start/Stop/Swap 抢同一把锁。
    // 直接调 StopLockedAsync 会让 Tray 退出与在飞的 StartAsync 并发，最坏结果是一个我们不再持有的孤儿内核。
    public async ValueTask DisposeAsync()
    {
        // 评审小钉④：二次释放是 no-op——_lifecycle 已被第一次释放 Dispose 掉，再 WaitAsync 就是
        // ObjectDisposedException（Tray 退出路径上 `using`/手动释放混用会走到这里）。
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        await InLifecycle(async () => { await StopLockedAsync(); return true; }, CancellationToken.None);
        _lifecycle.Dispose();
    }
}

public readonly record struct SwapOutcome(bool CoreRunningAfter, bool RolledBack)
{
    // 样例的工厂名 `RolledBack` 与位置参数生成的属性同名（CS0102：类型已包含 RolledBack 的定义），
    // 改成 Rollback；属性名/线语义不变。
    public static SwapOutcome Done(bool running) => new(running, false);
    public static SwapOutcome Rollback(bool running) => new(running, true);
}
