using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ClashAcry.Contracts;
using ClashAcry.Tray;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace ClashAcry.Core.Tests;

/// <summary>
/// 路由的测试夹具：临时 AppPaths + 真 SettingsStore + WireMock 冒充 controller 应答面 +
/// 假内核（.cmd，经 launchFactory 注入）+ 真 TrayCommandRouting。
/// 三条夹具级红线（都来自派发前裁定）：
/// ① R-10h——构造即取 <see cref="RegistrySnapshot"/>，因为 <c>sysproxy.set</c> 与 <c>core.stop</c> 会真写 HKCU，
///    而本机 <c>ProxyServer</c> 是别的软件留下的 <c>127.0.0.1:8090</c>，绝不允许被冲掉；
/// ② R-10i——必须自带 <see cref="FakeProbe"/> 把 controller 端口占用预检报成 false，否则 WireMock 自己
///    就占着那个端口，<c>CoreManager</c> 的预检会把「启动内核」这条成功路径当场拒掉（Task 8 的 R-8a 原样复发）；
/// ③ R-10e——注入的 <c>focusUi</c> 只记录调用次数，绝不接到真 <c>Process.Start</c>。
/// </summary>
public sealed class TrayHarness : IDisposable
{
    // Task 8 实测：cmd.exe 不肯执行「扩展名 .exe、内容是批处理」的文件，所以假内核是 .cmd，
    // 由 Settings.CorePath 指过去；挂起用 `ping -n 60`（timeout.exe 会被 Git Bash 的 coreutils 抢先）。
    private const string FakeCoreScript = "@echo off\r\necho start\r\nping -n 60 127.0.0.1 >nul\r\n";
    // 订阅导入需要一份真能过 ConfigInjector 的 Clash yaml（顶层键 + 其余原样透传）。
    private const string MinimalSubscriptionYaml = """
        port: 0
        socks-port: 0
        proxies:
          - name: harness-node
            type: direct
        """;

    private readonly string _dir = Directory.CreateTempSubdirectory("ca-tray-").FullName;
    private readonly RegistrySnapshot _registry;          // R-10h
    private readonly TcpListener _mixed;
    private readonly WireMockServer _wire = WireMockServer.Start();
    private readonly TaskCompletionSource<bool> _probeGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _probeEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public AppPaths Paths { get; }
    public SettingsStore Store { get; }
    public MihomoClient Client { get; }
    public HttpClient Http { get; } = new() { Timeout = TimeSpan.FromSeconds(10) };
    public CoreManager Core { get; }
    public PipeServer Pipe { get; }
    public TrayCommandRouting Routing { get; }
    public FakeProbe Probe { get; }
    public WireMockServer Wire => _wire;
    public string WireUrl => _wire.Url!;
    public int MixedPort { get; }

    /// R-10e：ui.focus 的动作次数。测试读它，既不起进程也不爆栈。
    public int FocusUiCalls { get; private set; }

    /// core.update 的桩结果（R-10a：真 CoreUpdater 是 Task 11 的类型，路由只依赖委托缝）。
    public UpdateResult UpdateStubResult { get; set; } = UpdateResult.AlreadyLatest;

    public TrayHarness() : this(gatedProbe: false) { }

    /// <param name="gatedProbe">true = ControllerAlive 卡在闸门上，由测试决定何时放行（R-10i 的重叠期闸门）。</param>
    public TrayHarness(bool gatedProbe)
    {
        _registry = new RegistrySnapshot();

        Paths = AppPaths.ForTest(_dir);
        Paths.EnsureCreated();

        _wire.Reset();
        _wire.Given(Request.Create().WithPath("/version").UsingGet()).RespondWith(
            Response.Create().WithBody(JsonSerializer.Serialize(new { meta = false, version = "v9.9.9-test" })));
        // spec §4.2：切模式是 PATCH /configs（v1 统一用 PATCH）。没有这条映射时 WireMock 回 404，
        // 客户端按契约抛 ApiException，mode.set 根本走不到断言。
        _wire.Given(Request.Create().WithPath("/configs").UsingPatch()).RespondWith(
            Response.Create().WithStatusCode(204));
        _wire.Given(Request.Create().WithPath("/sub").UsingGet()).RespondWith(
            Response.Create().WithStatusCode(200).WithBody(MinimalSubscriptionYaml, "text/plain"));

        // R-10h：随机空闲端口，别用 7897——那正是真 Tray 的默认 mixed 端口。
        _mixed = new TcpListener(IPAddress.Loopback, 0);
        _mixed.Start();
        MixedPort = ((IPEndPoint)_mixed.LocalEndpoint).Port;

        var fakeCore = Path.Combine(Paths.CoreDir, "fake-core.cmd");
        File.WriteAllText(fakeCore, FakeCoreScript);

        Store = new SettingsStore(Paths);
        Store.Save(Store.Load() with
        {
            ControllerHost = "127.0.0.1",
            ControllerPort = _wire.Port,
            MixedPort = MixedPort,
            CorePath = fakeCore,
        });

        Client = new MihomoClient(Http, "127.0.0.1", _wire.Port, Store.Load().Secret);
        Probe = new FakeProbe(Client, gatedProbe ? _probeGate.Task : null, _probeEntered);
        Core = new CoreManager(Paths, Store, Client, Probe, CmdLaunch);

        // 夹具里的 PipeServer 从不 StartAsync（没有客户端），只为让路由的 Broadcast/ClientCount 有真对象可读。
        Pipe = new PipeServer(_ => Task.FromResult<Frame?>(null));
        Routing = new TrayCommandRouting(Core, Store, Client, Paths, Http,
            (p, ct) => Task.FromResult(UpdateStubResult), Pipe, () => FocusUiCalls++);
    }

    public static ProcessStartInfo CmdLaunch(string exe) => new("cmd.exe", $"/c \"{exe}\"")
    { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };

    /// <summary>每条命令的合法 payload（简报 Step 1 的夹具契约）；unknown 之外不许有命令拿到 null 就跳过动作。</summary>
    public JsonElement? PayloadFor(string cmd) => cmd switch
    {
        CmdTypes.ModeSet => JsonUtil.Element(new { mode = "global" }),
        CmdTypes.SysProxySet => JsonUtil.Element(new { enabled = true }),
        CmdTypes.SettingsUpdate => JsonUtil.Element(new { mixed_port = 7899 }),
        CmdTypes.SubImport => JsonUtil.Element(new ImportRequest(WireUrl + "/sub")),
        _ => null,
    };

    /// <summary>放行卡在闸门上的那次 ControllerAlive（仅 gatedProbe 夹具有效）。</summary>
    public void ReleaseProbeGate() => _probeGate.TrySetResult(true);

    /// <summary>第一次进入 ControllerAlive 时完成（用来把「第二个请求在第一个还没探完时进来」钉成确定时序）。</summary>
    public Task ProbeEntered => _probeEntered.Task;

    public void Dispose()
    {
        // **夹具必须被 using 掉**（派发样例的 `var h = new TrayHarness();` 少了 using——xUnit 只释放它自己
        // 创建的测试类实例与 fixture，测试方法里 new 出来的对象没人管。实测代价本机踩过：
        // sysproxy.set 那条把 ProxyEnable 留在 1、ProxyServer 冲成随机空闲端口，后续用例的 HTTP 全走这个
        // 不存在的代理而超时，而用户真机上的 8090 串被冲掉了）。
        // 注册表还原放在 finally：前面任何一步抛错都不许把用户的系统代理留在开启状态。
        try
        {
            // 假内核可能还活着（core.start / core.reload 用例）。这里绝不调 Core.DisposeAsync()——
            // 那是个真 await，在同步 Dispose 里只能靠阻塞取值，而 xUnit1031 会把测试里的阻塞取值变成编译错误。
            // 按 CoreManager 公开的 RunningProcessId 同步杀整棵树（cmd.exe + 它的 ping.exe 子进程），不留孤儿。
            if (Core.RunningProcessId is int pid)
            {
                try { using var p = Process.GetProcessById(pid); p.Kill(entireProcessTree: true); }
                catch (Exception) { /* 已经自己退了 */ }
            }

            _mixed.Stop();
            _wire.Dispose();
            // 从未 StartAsync 的 PipeServer：DisposeAsync 里没有可达的 await，同步就走完了，不需要（也不许）阻塞取值。
            _ = Pipe.DisposeAsync();
        }
        finally
        {
            _registry.Dispose();   // 把 sysproxy.set 真写进去的 HKCU 值还原（本机 ProxyServer=127.0.0.1:8090 属于别的软件）
            try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>
    /// 裁定 R-10i（Task 8 的 R-8a 原样复发）：WireMock 常驻在 controller 端口上冒充内核应答面，
    /// 而 StartAsync 的预检用真实 TcpAlive 探那个端口，于是「端口被别的进程占用」必然为真、启动被自己拒掉。
    /// </summary>
    public sealed class FakeProbe : CoreProbe
    {
        private readonly Task<bool>? _gate;
        private readonly TaskCompletionSource<bool> _entered;

        public FakeProbe(MihomoClient client, Task<bool>? gate, TaskCompletionSource<bool> entered) : base(client)
            => (_gate, _entered) = (gate, entered);

        public override async Task<bool> ControllerAlive(CancellationToken ct)
        {
            _entered.TrySetResult(true);
            // 闸门**不带 ct**：时机由测试掌控。带上的话第二个请求的取消会在进函数前就落，
            // 测试空转（Task 8 R-8h 的同一条教训）。
            if (_gate is not null) await _gate;
            return await base.ControllerAlive(ct);
        }

        public override Task<bool> ControllerPortOccupied(string host, int port, CancellationToken ct)
            => Task.FromResult(false);
    }
}
