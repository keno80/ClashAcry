using System.Net;
using System.Net.Sockets;
using ClashAcry.Contracts;
using ClashAcry.Core;

namespace ClashAcry.UI.Tests.Fakes;

/// <summary>
/// 测试进程里的假 Tray：起一台真 <see cref="PipeServer"/>（生产管名），UI 的探活直接命中它。
/// 有了它，<c>Crashed</c>、进度推进这类**事件驱动**的状态才可能在无人点击的情况下被断言。
/// 三条夹具纪律：
/// ① 回包形状必须是各命令约定的 record——回成请求 payload 会让 UI 的反序列化当场抛，
///    测试就变成在测 PipeServer 而不是测界面；
/// ② <c>core.update</c> 的回包**故意压住**，直到测试调 <see cref="CompleteUpdate"/>：真更新是分钟级，
///    立刻回包等于把「进行中反馈」的中段抹掉（spec §9「结果帧才是 UI 的终点」）；
/// ③ handshake 里的 controller 端口现取一个空闲端口，绝不写死 9097——本机可能正有别家的 mihomo 在听，
///    界面一旦认为内核 Running 就会把 Bearer 打到别人的控制器上。
/// 管道名固定 ⇒ 同一时刻只能有一个假 Tray，UI.Tests 因此整体串行（AssemblyInfo）。
/// </summary>
public sealed class FakeTray : IAsyncDisposable
{
    private readonly PipeServer _server;
    private readonly TaskCompletionSource<UpdateResult> _update = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<Frame> _commands = new();

    public HandshakeReply Handshake { get; set; }
    public SettingsSnapshot Snapshot { get; set; }
    public ImportReply Import { get; set; } = new(@"C:\fake\runtime\config.yaml", null);

    /// <summary>覆盖默认回包（返回 null = 按约定不回，测「托盘无应答」时用）。</summary>
    public Func<Frame, Task<Frame?>>? Responder { get; set; }

    private FakeTray(int freeControllerPort)
    {
        _server = new PipeServer(DispatchAsync);
        Handshake = new HandshakeReply(CoreState.Stopped, "127.0.0.1", freeControllerPort,
            "fake-secret", 18999, "v-test", "http://www.gstatic.com/generate_204");
        Snapshot = new SettingsSnapshot(false, 18999, Handshake.DelayTestUrl, "v-test");
    }

    public static async Task<FakeTray> StartAsync()
    {
        var tray = new FakeTray(FreePort());
        await tray._server.StartAsync(default);       // 返回 = 第一台实例已挂上命名空间（R-9a），循环自己常驻
        return tray;
    }

    private async Task<Frame?> DispatchAsync(Frame f)
    {
        lock (_commands) _commands.Add(f);
        if (Responder is { } custom) return await custom(f);

        object payload = f.Type switch
        {
            CmdTypes.Handshake => Handshake,
            CmdTypes.SettingsGet or CmdTypes.SettingsUpdate => Snapshot,
            CmdTypes.SubImport => Import,
            // 压住回包直到测试放行；CompleteUpdate 同时广播 result——真 Tray 两条都发（既广播也回帧）。
            CmdTypes.CoreUpdate => await _update.Task,
            _ => true,
        };
        return f with { Payload = JsonUtil.Element(payload) };
    }

    public void CompleteUpdate(UpdateResult result)
    {
        _update.TrySetResult(result);
        _server.Broadcast(EventTypes.UpdateResult, result);
    }

    public void Emit(string type, object payload) => _server.Broadcast(type, payload);

    /// <summary>界面是否已经连上这条管道（连上 ≠ 握过手，握手由 UI 自己在 Loaded 后发）。</summary>
    public async Task<bool> WaitUiConnectedAsync(TimeSpan limit)
    {
        var deadline = DateTime.UtcNow + limit;
        while (DateTime.UtcNow < deadline)
        {
            if (_server.ClientCount > 0) return true;
            await Task.Delay(50);
        }
        return false;
    }

    /// <summary>收到过某条命令没有。记录由管道的 Pump 线程追加，读取必须同锁。</summary>
    public bool Saw(string commandType)
    {
        lock (_commands) return _commands.Any(c => c.Type == commandType);
    }

    /// <summary>命令流水的快照（断言「轮询真的发生了」或「请求体带了什么」用）。</summary>
    public IReadOnlyList<Frame> CommandLog
    {
        get { lock (_commands) return _commands.ToArray(); }
    }

    public async ValueTask DisposeAsync() => await _server.DisposeAsync();

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
