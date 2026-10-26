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

    private CoreManager Build(string script, int mixedPort, int controllerPort, bool portOccupied = false)
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
        return new CoreManager(paths, store, client, new FakeProbe(client, portOccupied), CmdLaunch);
    }

    // 裁定 R-8a：夹具里 WireMock 常驻在 controller 端口上冒充内核的应答面，所以"端口被别的进程占了"
    // 这件事必须由注入点表达，不能靠裸 TcpAlive——否则成功路径会被预检自己拒掉（样例原来就是这样的，
    // 三条测试必红）。生产路径不传这个类，预检退回真实的 TcpAlive。
    private sealed class FakeProbe(MihomoClient client, bool portOccupied) : CoreProbe(client)
    {
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

        Assert.False(await mgr.StartAsync(default));
        Assert.Equal(CoreState.Crashed, mgr.State);
        Assert.Contains("mixed", mgr.StateChangedEvents.Last().Reason);
    }

    [Fact]
    public async Task Start_FailsWhenVersionNeverResponds_AndKeepsStderrReason()
    {
        _wire.Reset();                       // /version 不再返回 200
        using var mixed = new TcpListener(IPAddress.Loopback, 0); mixed.Start();
        var mgr = Build(KernelLogScript("config file error: bad dns section"), Port(mixed), _wire.Port);

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
    public async Task Start_ControllerPortAlreadyOwned_Refuses_AndNeverSpawns()
    {
        // 裁定 R-8a：上一个内核的孤儿还占着 9097 时，绝不能 spawn 第二个——
        // 双探活会读到**旧内核**的应答，UI 报 Running、Stop 只杀我们自己那个，
        // 用户流量继续走一份看不见的旧配置（spec §9 状态失真）。
        var mgr = Build(SleepScript(), FreePort(), _wire.Port, portOccupied: true);

        Assert.False(await mgr.StartAsync(default));

        Assert.Equal(0, mgr.SpawnCount);                  // 一次都没起进程
        Assert.Equal(CoreState.Crashed, mgr.State);
        Assert.Contains("占用", mgr.StateChangedEvents.Last().Reason);
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

    private static void TryDelete(string dir)
    { try { Directory.Delete(dir, true); } catch { } }
}
