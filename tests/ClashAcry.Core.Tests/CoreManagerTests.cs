using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ClashAcry.Contracts;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace ClashAcry.Core.Tests;

// 裁定 R-8g：Stop 会经 SystemProxy.CleanupIfStale 真写 HKCU，两条 Stop_* 用例还直接 Enable()。
// 系统代理是进程外全局态，必须和 SystemProxyTests 同属独占集合——否则两个类并发会互相踩
// 备份/还原（Task 7 的 RegistrySnapshot 只在集合内串行时才成立）。
[Collection("RegistryGuard")]
public class CoreManagerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ca-core-").FullName;
    private readonly WireMockServer _wire = WireMockServer.Start();

    public CoreManagerTests()
    {
        _wire.Reset();
        _wire.Given(Request.Create().WithPath("/version").UsingGet()).RespondWith(
            Response.Create().WithBody(JsonSerializer.Serialize(new { meta = false, version = "v1.0.0" })));
    }
    public void Dispose() { _wire.Dispose(); TryDelete(_dir); }

    // 假内核：把 .cmd 脚本当内核跑（CoreManager 的 launchFactory 注入点）
    private static ProcessStartInfo CmdLaunch(string exe) => new("cmd.exe", $"/c \"{exe}\"")
    { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
    // Task 1 实测：真内核（mihomo v1.19.33）的 Go 日志全部走 **stdout**，stderr 恒空。
    // 假内核照这一点写：日志行 echo 到 stdout。若这里还写 `1>&2`，测试只能证明 stderr 支路通了，
    // 证明不了真实内核那条主路会被采进环（环的 stdout 订阅漏掉时测试仍然全绿）。
    // 本机实测（Task 8）：挂起用 `ping -n 60 127.0.0.1`，不用样例的 `timeout /t 60 /nobreak`——
    // ① timeout.exe 会被 Git Bash PATH 里的 coreutils timeout 抢先（"invalid time interval '/t'"，exit 125），
    // ② 真 timeout 在 stdin 不是控制台时（测试主机就是这种）立刻退出。两者都让"活着的假内核"变成秒退。
    private static string SleepScript() => "@echo off\r\necho start\r\nping -n 60 127.0.0.1 >nul\r\n";
    private static string KernelLogScript(string text) => $"@echo off\r\necho {text}\r\nping -n 60 127.0.0.1 >nul\r\n";
    private static string ExitScript() => "@echo off\r\necho boom\r\nexit 3\r\n";

    private int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0); l.Start();
        var p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p;
    }
    private static int Port(TcpListener l) => ((IPEndPoint)l.LocalEndpoint).Port;

    /// <summary>Build 造出来的探针，供用例在启动之后改它的口供（feat/core-liveness 的闸门/泵用例要它）。</summary>
    private FakeProbe? LastProbe;

    private CoreManager Build(string script, int mixedPort, int controllerPort, bool portOccupied = false,
                              TaskCompletionSource<bool>? aliveGate = null, Exception? aliveThrows = null,
                              TimeSpan? fastWindow = null, TimeSpan? startGrace = null,
                              Func<bool>? controllerReady = null)
    {
        var paths = AppPaths.ForTest(_dir); paths.EnsureCreated();
        // 本机实测（Task 8）：cmd.exe 不肯执行「扩展名是 .exe、内容是批处理」的文件（报"不是有效的
        // Windows 版本兼容的应用程序"、exit 1），所以假内核写成交给 Settings.CorePath 的 .cmd。
        // CoreManager 取的仍是 `s.CorePath ?? _paths.CoreExecutable`，生产默认分支不变。
        var fakeCore = Path.Combine(paths.CoreDir, "fake-core.cmd");
        File.WriteAllText(fakeCore, script);
        var store = new SettingsStore(paths);
        store.Save(store.Load() with { ControllerPort = controllerPort, MixedPort = mixedPort, CorePath = fakeCore });
        var client = new MihomoClient(new HttpClient { Timeout = TimeSpan.FromSeconds(2) },
            "127.0.0.1", controllerPort, store.Load().Secret);
        var probe = new FakeProbe(client, portOccupied, aliveGate, aliveThrows, controllerReady);
        LastProbe = probe;
        return new CoreManager(paths, store, client, probe, CmdLaunch, fastWindow, startGrace);
    }

    // 裁定 R-8a：夹具里 WireMock 常驻在 controller 端口上冒充内核的应答面，所以"端口被别的进程占了"
    // 这件事必须由注入点表达，不能靠裸 TcpAlive——否则成功路径会被预检自己拒掉（样例原来就是这样的，
    // 三条测试必红）。生产路径不传这个类，预检退回真实的 TcpAlive。
    private sealed class FakeProbe(MihomoClient client, bool portOccupied,
                                   TaskCompletionSource<bool>? aliveGate = null,
                                   Exception? aliveThrows = null,
                                   Func<bool>? controllerReady = null) : CoreProbe(client)
    {
        /// <summary>ControllerAlive 被调过几次（feat/core-liveness：Stopped 期不许探活）。</summary>
        public int ControllerAliveCalls;

        /// <summary>&gt;0 时每次探活扣一次并答"controller 死了"，之后恢复正常口供。</summary>
        public int FailAliveTimes;

        /// <summary>恒答"controller 死了"（进程还活着，只有应答面断了）。</summary>
        public bool ReportDead;

        /// <summary>探活卡住不返回（只听从传入的 ct）：用来把判死通路的**预算**钉出来。</summary>
        public bool HangsOnControllerAlive;

        // 裁定 R-8h 的时序闸门：ControllerAlive 卡在这道闸上（**不带 ct**，时机由测试掌控），
        // 取消才能落在探活循环中间——进函数前就取消好的令牌会在 InLifecycle 的 WaitAsync 就抛，
        // 压根没 spawn，测试空转。
        public override async Task<bool> ControllerAlive(CancellationToken ct)
        {
            Interlocked.Increment(ref ControllerAliveCalls);
            if (HangsOnControllerAlive) { await Task.Delay(Timeout.Infinite, ct); return false; }
            if (ReportDead) return false;
            // feat/core-liveness 的闸门用例要「只有那一次探不到」：按次计的口供，不改进程本身死活。
            if (FailAliveTimes > 0) { Interlocked.Decrement(ref FailAliveTimes); return false; }
            // 慢启动的假内核：真内核在解析配置（下 geo 数据库）期间 controller 根本不应答，
            // 这里用「到某个时刻才算得通」表达，才能把窗口分档的那条支路跑出来。
            if (controllerReady is not null && !controllerReady()) return false;
            if (aliveGate is not null) await aliveGate.Task;
            if (aliveThrows is not null) throw aliveThrows;
            return await base.ControllerAlive(ct);
        }

        public override Task<bool> ControllerPortOccupied(string host, int port, CancellationToken ct)
            => Task.FromResult(portOccupied);
    }

    [Fact]
    public async Task Start_TransitionsToRunning_WhenVersionAndMixedPortAlive()
    {
        using var _ = new RegistrySnapshot();   // Stop 会清系统代理，测试必须还原真实 HKCU
        using var mixed = new TcpListener(IPAddress.Loopback, 0); mixed.Start();
        var mgr = Build(SleepScript(), Port(mixed), _wire.Port);

        Assert.True(await mgr.StartAsync(default));
        Assert.Equal(CoreState.Running, mgr.State);
        Assert.Contains(mgr.StateChangedEvents, e => e.State == CoreState.Running);
        await mgr.StopAsync(default);
    }

    [Fact]
    public async Task Start_DoesNotReachRunning_WhenControllerAliveButMixedPortDead()
    {
        // spec §9 状态失真：mixed 端口没人听 → 绝不进 Running
        var mgr = Build(SleepScript(), FreePort(), _wire.Port);

        // 裁定 R-8i③ 回归锁：窗口是墙上时间 5 s，不是轮数预算。旧写法（20 轮 × (250ms 间隔 + 1s 探活超时)）
        // 实测 ~50 s；阈值取 12 s = StartAsync 的两次尝试 × 5 s 窗口 + 一次重试的余量取一半——
        // R-8i 原文写的 8 s 漏算了「失败自动重试一次」会把两个 5 s 窗口串在同一次 StartAsync 里
        // （裁定自己的净效果行也是「~5 s/次尝试」），正确实现下本用例实测 ~10.3 s，8 s 必红。
        // 阈值仍锁得住「窗口悄悄改回轮数制」（~50 s）这类失真。
        var sw = Stopwatch.StartNew();
        var started = await mgr.StartAsync(default);
        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(12),
            $"探活窗口必须是墙上时间 5 s/次尝试（R-8i），实测失败路径 {sw.Elapsed.TotalSeconds:F1} s");

        Assert.False(started);
        Assert.Equal(CoreState.Crashed, mgr.State);
        Assert.Contains("mixed", mgr.StateChangedEvents.Last().Reason);
    }

    [Fact]
    public async Task Start_FailsWhenVersionNeverResponds_AndKeepsStderrReason()
    {
        _wire.Reset();                       // /version 不再返回 200
        using var mixed = new TcpListener(IPAddress.Loopback, 0); mixed.Start();
        // startGrace 收到 1 s：这条走的是「controller 一直不应答」的宽限档，生产默认是 120 s，
        // 不在测试里等它——宽限档本身的行为由 Start_SlowBoot_* 那两条钉。
        var mgr = Build(KernelLogScript("config file error: bad dns section"), Port(mixed), _wire.Port,
            startGrace: TimeSpan.FromSeconds(1));

        Assert.False(await mgr.StartAsync(default));
        Assert.Equal(CoreState.Crashed, mgr.State);
        Assert.Contains("探活失败", mgr.StateChangedEvents.Last().Reason);
        Assert.Contains(mgr.StateChangedEvents.Last().StderrTail, l => l.Contains("config file error"));
    }

    [Fact]
    public async Task Start_ExitsImmediately_ReportsExitCode_AndRetriesExactlyOnce()
    {
        var mgr = Build(ExitScript(), FreePort(), _wire.Port);

        Assert.False(await mgr.StartAsync(default));
        Assert.Equal(2, mgr.StartAttemptCount);           // 首发 + 一次重试，不多
        Assert.Equal(CoreState.Crashed, mgr.State);
        Assert.Contains("退出码 3", mgr.StateChangedEvents.Last().Reason);

        // 裁定 R-8e：重试本身是要显示的事实（spec §9「仅启动时自动重试一次」）。
        // 样例在 AttemptStart 顶部无条件清 _lastReason，会把这条叙述抹掉。
        var starts = mgr.StateChangedEvents.Where(e => e.State == CoreState.Starting).ToList();
        Assert.Equal(2, starts.Count);
        Assert.Contains("重试", starts[1].Reason);
    }

    [Fact]
    public async Task Start_SlowBoot_PastFastWindow_KeepsWaiting_AndReachesRunning()
    {
        // 真机踩到（2026-10-27，用户机场订阅里有 `GEOIP,CN,DIRECT`）：mihomo 必须先把 geoip.metadb 下完
        // 才开始监听任何端口，实测它自己给这次下载留了 90 s。旧实现 5 s 就判死并 KillProcess，
        // 把下载掐成半截 metadb → 下一次「MMDB invalid, remove and download」重下 → 再被掐死，
        // 用户看到的是「导入成功后内核永远起不来」。
        // 这条锁的是分档后的宽限支路：controller 不应答 **且进程还活着** = 还在启动，不是崩溃。
        using var _ = new RegistrySnapshot();
        using var mixed = new TcpListener(IPAddress.Loopback, 0); mixed.Start();
        var boot = Stopwatch.StartNew();
        var mgr = Build(SleepScript(), Port(mixed), _wire.Port,
            fastWindow: TimeSpan.FromMilliseconds(300), startGrace: TimeSpan.FromSeconds(5),
            controllerReady: () => boot.Elapsed >= TimeSpan.FromMilliseconds(900));

        Assert.True(await mgr.StartAsync(default));

        Assert.Equal(CoreState.Running, mgr.State);
        Assert.Equal(1, mgr.SpawnCount);                       // 没被提前杀，所以不需要第二次 spawn
        Assert.Contains(mgr.StateChangedEvents,
            e => e.State == CoreState.Starting && e.Reason is not null && e.Reason.Contains("仍在启动"));
        await mgr.StopAsync(default);
    }

    [Fact]
    public async Task Start_ControllerNeverBoots_GraceExpires_CrashesWithGraceInReason()
    {
        // 宽限不是无限等：进程活着但 controller 到 _startGrace 仍不应答，照旧判死（不留孤儿）。
        using var _ = new RegistrySnapshot();
        using var mixed = new TcpListener(IPAddress.Loopback, 0); mixed.Start();
        var mgr = Build(SleepScript(), Port(mixed), _wire.Port,
            fastWindow: TimeSpan.FromMilliseconds(200), startGrace: TimeSpan.FromSeconds(1),
            controllerReady: () => false);

        Assert.False(await mgr.StartAsync(default));

        Assert.Equal(CoreState.Crashed, mgr.State);
        Assert.Contains("1 s 内未就绪", mgr.StateChangedEvents.Last().Reason);
    }

    [Fact]
    public async Task Start_ControllerPortAlreadyOwned_Refuses_AndNeverSpawns()
    {
        // 裁定 R-8a：上一个内核的孤儿还占着 9097 时，绝不能 spawn 第二个——
        // 双探活会读到**旧内核**的应答，UI 报 Running、Stop 只杀我们自己那个，
        // 用户流量继续走一份看不见的旧配置（spec §9 状态失真）。
        var mgr = Build(SleepScript(), FreePort(), _wire.Port, portOccupied: true);

        Assert.False(await mgr.StartAsync(default));

        Assert.Equal(0, mgr.SpawnCount);                  // 一次都没起进程
        Assert.Equal(2, mgr.StartAttemptCount);           // 评审小钉③：占用被拒也吃一次重试（R-8a 明写过）
        Assert.Equal(CoreState.Crashed, mgr.State);
        Assert.Contains("占用", mgr.StateChangedEvents.Last().Reason);
    }

    [Fact]
    public async Task StartCancelled_KillsCore_TransitionsStopped_AndThrowsWithoutRetrying()
    {
        // 裁定 R-8h + spec §9 取消语义：调用方取消不是「启动失败」。三步顺序不可换——
        // 杀掉已 spawn 的进程（绝不留孤儿内核）→ Transition(Stopped) → 原样抛 OperationCanceledException，
        // 且不许拿着已取消的令牌再跑第二轮重试。
        using var _ = new RegistrySnapshot();
        using var mixed = new TcpListener(IPAddress.Loopback, 0); mixed.Start();
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var mgr = Build(SleepScript(), Port(mixed), _wire.Port, aliveGate: gate);
        using var cts = new CancellationTokenSource();

        var start = mgr.StartAsync(cts.Token);            // 故意不 await：要让它停在探活循环中间
        var spawnDeadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < spawnDeadline && (mgr.SpawnCount != 1 || mgr.State != CoreState.Starting))
            await Task.Delay(20);
        Assert.Equal(1, mgr.SpawnCount);
        Assert.Equal(CoreState.Starting, mgr.State);

        cts.Cancel();
        gate.SetResult(false);                            // 放行被卡住的那次探活

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
        Assert.Equal(CoreState.Stopped, mgr.State);        // 不是 Crashed，也不是卡在 Starting
        Assert.Equal(1, mgr.SpawnCount);                   // 重试循环不许再 spawn 第二个
        Assert.Equal(1, mgr.StartAttemptCount);
        Assert.Contains("启动被取消", mgr.StateChangedEvents.Last().Reason);
    }

    [Fact]
    public async Task StartProbeThrowsTaskCanceled_WithUncancelledToken_StillFallsToStartFailure()
    {
        // R-8h 的另一半：`when (ct.IsCancellationRequested)` 这道闸门必须有回归锁。
        // HttpClient 超时是以 TaskCanceledException（OperationCanceledException 的子类）冒出来的，
        // 令牌没取消时它是**真的启动失败**——必须照旧重试后落 Crashed，不许被取消语义那条 catch 接走。
        using var _ = new RegistrySnapshot();
        using var mixed = new TcpListener(IPAddress.Loopback, 0); mixed.Start();
        var mgr = Build(SleepScript(), Port(mixed), _wire.Port,
            aliveThrows: new TaskCanceledException("simulated controller timeout"));

        Assert.False(await mgr.StartAsync(default));          // 返回 false，不是抛
        Assert.Equal(CoreState.Crashed, mgr.State);           // 不是 Stopped
        Assert.Equal(2, mgr.StartAttemptCount);               // 吃的正是 spec §9 那一次自动重试
        Assert.Equal(2, mgr.SpawnCount);
        await mgr.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_KillsCoreAndLeavesStopped()
    {
        // 裁定 R-8c 的回归锁（评审小钉④：此前全套没有一处调用 DisposeAsync）：DisposeAsync 是公开入口，
        // 必须和 Start/Stop 抢同一把 _lifecycle 并真把内核杀掉；二次释放是 no-op（不许抛
        // ObjectDisposedException——_lifecycle.Dispose() 已经发生过）。
        using var _ = new RegistrySnapshot();
        using var mixed = new TcpListener(IPAddress.Loopback, 0); mixed.Start();
        var mgr = Build(SleepScript(), Port(mixed), _wire.Port);
        await mgr.StartAsync(default);
        var pid = mgr.RunningProcessId!.Value;

        await mgr.DisposeAsync();

        Assert.Equal(CoreState.Stopped, mgr.State);
        Assert.Null(mgr.RunningProcessId);
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline && Process.GetProcesses().Any(p => p.Id == pid)) await Task.Delay(100);
        Assert.DoesNotContain(Process.GetProcesses(), p => p.Id == pid);

        await mgr.DisposeAsync();                          // 二次释放
        Assert.Equal(CoreState.Stopped, mgr.State);
        Assert.Equal(1, mgr.StateChangedEvents.Count(e => e.State == CoreState.Stopped));   // no-op 不许再广播
    }

    [Fact]
    public async Task Stop_LeavesForeignSystemProxyAlone()
    {
        // 裁定 R-8b：「停止内核顺带清代理」只许清指向自己的那份。
        using var _ = new RegistrySnapshot();
        using var mixed = new TcpListener(IPAddress.Loopback, 0); mixed.Start();
        var mgr = Build(SleepScript(), Port(mixed), _wire.Port);
        await mgr.StartAsync(default);
        SystemProxy.Enable("127.0.0.1:1234", "");         // 别的软件的代理（本机真有一个 8090）

        await mgr.StopAsync(default);

        Assert.True(SystemProxy.IsEnabled());                     // 绝不替用户关别人的代理
        Assert.Equal("127.0.0.1:1234", SystemProxy.CurrentServer());
    }

    [Fact]
    public async Task Stop_KillsCoreProcessAndDisablesSystemProxy()
    {
        using var _ = new RegistrySnapshot();
        using var mixed = new TcpListener(IPAddress.Loopback, 0); mixed.Start();
        var mgr = Build(SleepScript(), Port(mixed), _wire.Port);
        await mgr.StartAsync(default);
        var pid = mgr.RunningProcessId!.Value;
        SystemProxy.Enable($"127.0.0.1:{mgr.MixedPort}", "");

        await mgr.StopAsync(default);

        Assert.Equal(CoreState.Stopped, mgr.State);
        Assert.False(SystemProxy.IsEnabled());
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline && Process.GetProcesses().Any(p => p.Id == pid)) await Task.Delay(100);
        Assert.DoesNotContain(Process.GetProcesses(), p => p.Id == pid);
        Assert.Null(mgr.RunningProcessId);   // Stop 后必须清空，避免留下陈旧 PID
    }

    [Fact]
    public async Task EveryTransition_EmitsEvent_AndNoStateIsSilent()
    {
        using var _ = new RegistrySnapshot();   // Stop 会清系统代理，测试必须还原真实 HKCU
        using var mixed = new TcpListener(IPAddress.Loopback, 0); mixed.Start();
        var mgr = Build(SleepScript(), Port(mixed), _wire.Port);
        var seen = new List<CoreState>();
        mgr.StateChanged += e => seen.Add(e.State);

        // 样例这里是 `void` + `.GetAwaiter().GetResult()`：xUnit1031 把它当阻塞式任务操作，
        // 在本仓 TreatWarningsAsErrors 下直接是编译错误。改成 async/await，断言与事件序列不变。
        await mgr.StartAsync(default);
        await mgr.StopAsync(default);

        Assert.Equal(new[] { CoreState.Starting, CoreState.Running, CoreState.Stopped }, seen);
    }

    [Fact]
    public async Task ConcurrentStart_GuardHitsSecondRequest_AndSpawnHappensOnce()
    {
        using var _ = new RegistrySnapshot();   // Stop 会清系统代理，测试必须还原真实 HKCU
        using var mixed = new TcpListener(IPAddress.Loopback, 0); mixed.Start();
        var mgr = Build(SleepScript(), Port(mixed), _wire.Port);
        var a = mgr.StartAsync(default); var b = mgr.StartAsync(default);

        Assert.True(await a); Assert.True(await b);
        Assert.Equal(1, mgr.SpawnCount);          // spec §3 禁令 1 的进程侧保证
        Assert.True(mgr.ConcurrentGuardHits >= 1);
        await mgr.StopAsync(default);
    }

    // ── Task 11：SwapExecutableAsync 是更新器唯一被授权碰内核进程的缝。它此前零覆盖，
    // 而两条真值口径只有测出来才成立（实施日在这条缝上发现并改掉一个真缺陷：回滚分支会无条件起核）。──

    [Fact]
    public async Task Swap_WhenStopped_ReplacesExe_AndNeverSpawnsCore()
    {
        using var _ = new RegistrySnapshot();
        var mgr = Build(SleepScript(), FreePort(), _wire.Port);
        var exe = AppPaths.ForTest(_dir).CoreExecutable;
        var seen = new List<CoreState>();
        mgr.StateChanged += e => seen.Add(e.State);

        var res = await mgr.SwapExecutableAsync(
            () => { File.WriteAllBytes(exe, new byte[] { 1, 2, 3 }); return Task.CompletedTask; }, default);

        Assert.False(res.RolledBack);
        // Done 的第一位是「换完之后真的在跑吗」，不是「操作成功」：v1 的语义是可预期的显式启停，
        // 换核不是启动。这条断言就是那处无条件起核缺陷的回归锁。
        Assert.False(res.CoreRunningAfter);
        Assert.Equal(0, mgr.SpawnCount);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(exe));
        Assert.Equal(CoreState.Stopped, mgr.State);
        Assert.Contains(CoreState.Updating, seen);        // spec §3 禁令 3：迁移点唯一出口且必须广播
    }

    [Fact]
    public async Task Swap_WhenReplaceFailsWhileStopped_RollsBackWithoutStartingCore()
    {
        using var _ = new RegistrySnapshot();
        var mgr = Build(SleepScript(), FreePort(), _wire.Port);
        var exe = AppPaths.ForTest(_dir).CoreExecutable;
        // 真机形态：跑着的 mihomo 由 OS 以 FILE_SHARE_READ 映射镜像；这里用同样的共享度占住目标，
        // 让换文件那一步抛 IOException（File.WriteAllBytes 要写访问，现有共享只读 → 共享冲突）。
        using var hold = new FileStream(exe, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);

        var res = await mgr.SwapExecutableAsync(
            () => { File.WriteAllBytes(exe, new byte[] { 9 }); return Task.CompletedTask; }, default);

        Assert.True(res.RolledBack);
        Assert.False(res.CoreRunningAfter);
        Assert.Equal(0, mgr.SpawnCount);                  // 一次失败的更新绝不多出一个用户没点过的内核进程
        Assert.Equal(CoreState.Stopped, mgr.State);
    }

    [Fact]
    public async Task Swap_WhileRunning_WhenTargetInUse_RestartsOldCoreAndReportsRolledBack()
    {
        using var _ = new RegistrySnapshot();
        using var mixed = new TcpListener(IPAddress.Loopback, 0); mixed.Start();
        var mgr = Build(SleepScript(), Port(mixed), _wire.Port);
        Assert.True(await mgr.StartAsync(default));
        var exe = AppPaths.ForTest(_dir).CoreExecutable;
        using var hold = new FileStream(exe, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);

        var res = await mgr.SwapExecutableAsync(
            () => { File.WriteAllBytes(exe, new byte[] { 9 }); return Task.CompletedTask; }, default);

        // spec §9「替换失败退回原 .exe 并保持内核运行」：换之前在跑，失败回滚后就该还在跑。
        Assert.True(res.RolledBack);
        Assert.True(res.CoreRunningAfter);
        Assert.Equal(CoreState.Running, mgr.State);
        Assert.Equal(2, mgr.SpawnCount);                  // 停了一次、回滚又起一次
        await mgr.StopAsync(default);
    }

    /// <summary>
    /// Task 17 真机跑出来的缺陷的回归锁：默认启动参数必须指向 **runtime 目录**（注入产物 config.yaml 在那儿）。
    /// 写成 Root 时 mihomo 在那里找不到配置，会拿空默认配置起来（监听 7890、连 external-controller 都没有），
    /// 双探活 controller+mixed 全 fail，用户看到「崩溃」而日志里没有一行错误。
    /// 之前每条用例都注入假的 launchFactory（cmd 脚本），这条默认分支从来没被真内核跑过。
    /// </summary>
    [Fact]
    public void DefaultLaunch_PointsAtTheDirThatActuallyHoldsConfigYaml()
    {
        var paths = AppPaths.ForTest(_dir); paths.EnsureCreated();
        File.WriteAllText(paths.ConfigFile, "mixed-port: 7897\n");            // 导入产物落在这里

        var psi = CoreManager.DefaultLaunch(paths, paths.CoreExecutable);
        var d = psi.Arguments[4..^1];                                          // 从 -d "<dir>" 里取回目录

        Assert.Equal(paths.RuntimeDir, d);
        Assert.True(File.Exists(Path.Combine(d, "config.yaml")),
            $"默认 -d 指向 {d}，那里没有 config.yaml——内核会按空配置起来，探活必炸");
        Assert.False(psi.UseShellExecute);
        Assert.True(psi.RedirectStandardOutput);                               // Task 1 实测：真内核日志走 stdout
    }

    // ── feat/core-liveness（spec §12 第六轮真机实测：用户在任务管理器里结束 mihomo，界面还写「运行中」）──
    // 三条判死通路各一条回归锁：① 进程自己报 Exit 事件；② 低频双探活泵；③ core.start 闸门先探活再回 true。

    [Fact]
    public async Task ExternalKill_WhileRunning_TransitionsCrashed_AndReportsExitCode()
    {
        // 现网缺陷：CoreManager 只在**启动窗口内**盯 `_proc.HasExited`，进了 Running 就把这棵树交了。
        // 真机复现——起核 → 外部结束 mihomo → 状态永远停在 Running，`/version` 早已无人应答。
        using var _ = new RegistrySnapshot();
        using var mixed = new TcpListener(IPAddress.Loopback, 0); mixed.Start();
        var mgr = Build(SleepScript(), Port(mixed), _wire.Port);
        Assert.True(await mgr.StartAsync(default));
        var pid = mgr.RunningProcessId!.Value;

        // 「外部」结束：另取一个 Process 句柄杀整棵树，不走我们自己的 KillProcess。
        using (var other = Process.GetProcessById(pid)) other.Kill(entireProcessTree: true);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && mgr.State != CoreState.Crashed) await Task.Delay(50);

        Assert.Equal(CoreState.Crashed, mgr.State);
        Assert.Contains("退出码", mgr.StateChangedEvents.Last().Reason);
        Assert.DoesNotContain(Process.GetProcesses(), p => p.Id == pid);
        await mgr.StopAsync(default);
    }

    [Fact]
    public async Task Start_WhileRunningButLivenessProbeFails_RespawnsInsteadOfClaimingSuccess()
    {
        // spec §3 禁令 3：「点下去不等于成功」。现网 `StartLockedAsync` 顶部的闸门只看状态机自己说什么，
        // 于是状态失真时 `core.start` 回包 `true`、内核其实没跑，UI 收到「成功」却等不到任何状态事件。
        using var _ = new RegistrySnapshot();
        using var mixed = new TcpListener(IPAddress.Loopback, 0); mixed.Start();
        var mgr = Build(SleepScript(), Port(mixed), _wire.Port);
        Assert.True(await mgr.StartAsync(default));
        var oldPid = mgr.RunningProcessId!.Value;
        Assert.Equal(1, mgr.SpawnCount);
        // 只有闸门那一次答「controller 不应答」，重新 spawn 之后照旧应答。
        LastProbe!.FailAliveTimes = 1;

        Assert.True(await mgr.StartAsync(default));

        Assert.Equal(2, mgr.SpawnCount);                       // 闸门探不到就真重起，而不是回 true 谎报
        Assert.Equal(CoreState.Running, mgr.State);
        Assert.NotNull(mgr.RunningProcessId);
        Assert.NotEqual(oldPid, mgr.RunningProcessId!.Value);  // 收了自己的尸再起新的，不留孤儿
        await mgr.StopAsync(default);
    }

    [Fact]
    public async Task CheckLiveness_IsFalseWhenMixedPortDiesEvenThoughControllerAnswers()
    {
        // §9 双探活口径在判死通路上的复用：controller 应答而 mixed 没人听 = 不算活着（带病存活）。
        using var _ = new RegistrySnapshot();
        var mixed = new TcpListener(IPAddress.Loopback, 0); mixed.Start();
        var port = Port(mixed);
        var mgr = Build(SleepScript(), port, _wire.Port);
        Assert.True(await mgr.StartAsync(default));
        mixed.Stop();                                          // 假内核还活着，只是 mixed 端口没了

        Assert.False(await mgr.CheckLivenessAsync(default));
        await mgr.StopAsync(default);
    }

    [Fact]
    public async Task CheckLiveness_HangsAreCappedByOneSecondBudget_AndReadAsDead()
    {
        // 裁定 R-8i 的同一条纪律落到判死通路上：一次 controller 探活最多 1 s，
        // 否则「controller 端口挂着但不答」会把低频泵的每一轮拖成 HttpClient 的 30 s（Tray 里再也不能有假死）。
        using var _ = new RegistrySnapshot();
        using var mixed = new TcpListener(IPAddress.Loopback, 0); mixed.Start();
        var mgr = Build(SleepScript(), Port(mixed), _wire.Port);
        Assert.True(await mgr.StartAsync(default));
        LastProbe!.HangsOnControllerAlive = true;

        var sw = Stopwatch.StartNew();
        var alive = await mgr.CheckLivenessAsync(default);
        sw.Stop();

        Assert.False(alive);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3),
            $"controller 探活必须有 1 s 预算，实测挂了 {sw.Elapsed.TotalSeconds:F1} s");
        LastProbe.HangsOnControllerAlive = false;
        await mgr.StopAsync(default);
    }

    [Fact]
    public async Task LivenessPump_TwoConsecutiveDeadRounds_TransitionsCrashed()
    {
        using var _ = new RegistrySnapshot();
        using var mixed = new TcpListener(IPAddress.Loopback, 0); mixed.Start();
        var mgr = Build(SleepScript(), Port(mixed), _wire.Port);
        Assert.True(await mgr.StartAsync(default));
        LastProbe!.ReportDead = true;
        using var cts = new CancellationTokenSource();
        new CoreLivenessPump(mgr, TimeSpan.FromMilliseconds(50)).Start(cts.Token);

        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline && mgr.State != CoreState.Crashed) await Task.Delay(50);

        Assert.Equal(CoreState.Crashed, mgr.State);
        Assert.Contains("连续", mgr.StateChangedEvents.Last().Reason);
        // 判死不许顺手杀进程：控制器只是不答时，误杀会把「说不清」变成「真断了」。
        Assert.NotNull(mgr.RunningProcessId);
        await mgr.StopAsync(default);
    }

    [Fact]
    public async Task LivenessPump_OneDeadRoundThenRecovery_KeepsRunning()
    {
        // 连续两轮才判死（最坏 ~10 s 说实话）：真机上一轮探活失败太容易是网络/GC 的毛刺，
        // 单轮判死会把还在跑的内核说成崩了，用户平白丢一张界面。
        using var _ = new RegistrySnapshot();
        using var mixed = new TcpListener(IPAddress.Loopback, 0); mixed.Start();
        var mgr = Build(SleepScript(), Port(mixed), _wire.Port);
        Assert.True(await mgr.StartAsync(default));
        LastProbe!.FailAliveTimes = 1;
        using var cts = new CancellationTokenSource();
        new CoreLivenessPump(mgr, TimeSpan.FromMilliseconds(50)).Start(cts.Token);

        await Task.Delay(600);                                 // 远超两轮，且第二轮已经恢复正常口供

        Assert.Equal(CoreState.Running, mgr.State);
        await mgr.StopAsync(default);
    }

    [Fact]
    public async Task LivenessPump_WhenStopped_NeverProbes_AndCancellationStopsIt()
    {
        // 低频泵只在 Running 探：Stopped/Starting/Crashed/Updating 期探活答"死"是常态，
        // 照探就会在每个正常状态上误判死；取消后也不许继续留一条后台循环。
        using var _ = new RegistrySnapshot();
        var mgr = Build(SleepScript(), FreePort(), _wire.Port);
        using var cts = new CancellationTokenSource();
        new CoreLivenessPump(mgr, TimeSpan.FromMilliseconds(50)).Start(cts.Token);

        await Task.Delay(400);
        Assert.Equal(0, LastProbe!.ControllerAliveCalls);      // 没起内核，一次都不该探

        cts.Cancel();
        await Task.Delay(200);
        Assert.Equal(CoreState.Stopped, mgr.State);
    }

    private static void TryDelete(string dir)
    { try { Directory.Delete(dir, true); } catch { } }
}
