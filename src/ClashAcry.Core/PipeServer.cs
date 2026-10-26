using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using ClashAcry.Contracts;

namespace ClashAcry.Core;

/// <summary>
/// UI ↔ Tray 的唯一进程间通道（spec §3 三条禁令、§4.1）。服务端只认帧、不认业务：
/// 命令怎么解释由 <see cref="_onCommand"/>（Task 10 接 CoreManager/路由表）决定，本类只管
/// 「请求—回复」与「一对多广播」两条流，以及连接的生命周期。
/// </summary>
public sealed class PipeServer : IAsyncDisposable
{
    private readonly Func<Frame, Task<Frame?>> _onCommand;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<ConnectedClient> _clients = new();
    private readonly object _instanceGate = new();
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _accept;
    private NamedPipeServerStream? _waitingInstance;   // R-9e：正在等连接的那台实例，取消时必须关掉它才打得断等待
    private int _disposed;                              // DisposeAsync 幂等：Task 10 的退出钩子可能连调两次
    private long _seq;

    public PipeServer(Func<Frame, Task<Frame?>> onCommand) => _onCommand = onCommand;

    public int ClientCount { get { lock (_clients) return _clients.Count; } }

    /// <summary>
    /// 裁定 R-9a：返回的是「第一台实例已挂上命名空间」，不是 accept 循环本身。
    /// 循环存进 _accept，只在 DisposeAsync 里 await；原样例把循环 Task 直接返回，await 它就等于等到超时。
    /// </summary>
    public async Task StartAsync(CancellationToken ct)
    {
        _accept ??= AcceptLoop(ct);
        await _ready.Task.WaitAsync(ct);
    }

    /// <summary>广播一帧给所有已连接客户端。只入队，绝不在调用线程上写管道（R-9b）。</summary>
    public void Broadcast(string type, object? payload)
    {
        var frame = new Frame("event", type, payload is null ? null : JsonUtil.Element(payload),
            Interlocked.Increment(ref _seq), Guid.NewGuid().ToString("N"), null, null);
        ConnectedClient[] snapshot;
        lock (_clients) snapshot = _clients.ToArray();
        foreach (var c in snapshot) c.Enqueue(frame);   // 裁定 R-9b：只入队；一个字节都不在锁里写、也不替慢客户端排队
    }

    private async Task AcceptLoop(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);   // 裁定 R-9e
        while (!linked.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try { pipe = CreateServerPipe(); }
            catch (Exception ex)
            {
                // 首台实例都建不起来时 StartAsync 必须带着真异常立刻返回，而不是挂到调用方的超时
                // （spec §9「无反馈卡死」）。已就绪过的循环（第二次及以后失败）只能就此收尾：
                // 本类的公开契约里没有错误回调，Task 10 的宿主日志在 DisposeAsync 那条路上看这个 Task 的异常。
                _ready.TrySetException(ex);
                return;
            }

            lock (_instanceGate)
            {
                // 关掉「已经 Dispose 过了」的竞态：取消已发生却还把新实例登记进去，就没人负责 Dispose 它，
                // DisposeAsync 的 await _accept 会挂在 WaitForConnection 上（R-9e 的坑换个姿势复现）。
                if (linked.IsCancellationRequested) { pipe.Dispose(); return; }
                _waitingInstance = pipe;
            }
            _ready.TrySetResult();

            try
            {
                await pipe.WaitForConnectionAsync(linked.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException
                                       or IOException)   // Dispose 掉实例时这里以异常结束，正是我们要的退出方式
            {
                pipe.Dispose();
                return;
            }
            finally { lock (_instanceGate) if (ReferenceEquals(_waitingInstance, pipe)) _waitingInstance = null; }

            var client = new ConnectedClient(pipe);
            lock (_clients) _clients.Add(client);
            _ = Pump(client, linked.Token);   // 每条连接一个 Pump；异常一律在 Pump 内部消化（R-9c）
        }
    }

    /// <summary>
    /// internal static：R-9f 的读回断言要直接拿到实例（配 csproj 里的 InternalsVisibleTo）。
    /// </summary>
    internal static NamedPipeServerStream CreateServerPipe()
    {
        var sec = new PipeSecurity();
        // 只允许当前用户（spec §4.1 红线：DACL 里没有 S-1-1-0 Everyone）。
        // CreateNewInstance(0x4) 是本机实测必须补的一笔：PipeAccessRights.ReadWrite 展开是 0x2019B，
        // **不含** 0x4，而 Windows 在建「同名管道的后续实例」时要按「已存在的那个管道对象」的 DACL
        // 校验 CREATE_PIPE_INSTANCE —— 只给 ReadWrite 时，第一个客户端连上之后第二台实例直接
        // UnauthorizedAccessException: Access to the path is denied.，accept 循环当场死掉、
        // 于是 UI 侧第二个客户端永远连不上（广播用例与「断开后重连」全红）。
        // 加这个右只放宽「同一用户自己再多开一台实例」，不引入任何其他 SID。
        sec.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!,
            PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));

        // 裁定 R-9f（本机实测）：只有 NamedPipeServerStreamAcl.Create 能在建实例时落 DACL。
        // 先 new 再 SetAccessControl 会抛 UnauthorizedAccessException，且读回仍是默认 5 条 Allow
        // （含 S-1-1-0 Everyone）——那样本机任意用户都能 handshake 把 controller secret 读走。
        // 注意 maxNumberOfServerInstances 传 255 会抛 ArgumentOutOfRangeException，这里用 MaxAllowedServerInstances。
        return NamedPipeServerStreamAcl.Create(ContractConstants.PipeName, PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, 0, 0, sec);
    }

    private async Task Pump(ConnectedClient client, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                Frame frame;
                try
                {
                    // 裁定 R-9c：这三型都不许逃出 Pump——它是 fire-and-forget，逃出去就是无声失败，
                    // Task 10 的日志全靠这条连接自己说清楚为什么断。InvalidDataException 派生自
                    // SystemException，不在 IOException 家族里（FrameCodec.cs:38 那句注释不实）。
                    frame = await FrameCodec.ReadLineAsync(client.Reader, ct)
                            ?? throw new InvalidDataException("eof");
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException)
                {
                    // 坏帧/EOF 只弄死这一条连接：不重新 spawn 任何东西，直接走 finally 摘除。
                    return;
                }

                Frame? reply;
                try { reply = await _onCommand(frame); }
                catch (Exception ex)
                { reply = frame with { Payload = null, Error = ex.Message, ReplyTo = frame.Id }; }
                if (reply is not null) client.Enqueue(reply with { Kind = "reply", ReplyTo = frame.Id });
            }
        }
        finally { lock (_clients) _clients.Remove(client); client.Dispose(); }
    }

    public async ValueTask DisposeAsync()
    {
        // 幂等：Task 10 的退出钩子与 await using 可能各调一次，第二次会在已 Dispose 的 CTS 上抛
        // ObjectDisposedException，把「干净退出」变成日志里的假故障。
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        _cts.Cancel();
        NamedPipeServerStream? waiting;
        lock (_instanceGate) { waiting = _waitingInstance; _waitingInstance = null; }
        waiting?.Dispose();   // 裁定 R-9e：句柄一关，挂在 WaitForConnectionAsync 上的循环立刻返回
        ConnectedClient[] clients;
        lock (_clients) { clients = _clients.ToArray(); _clients.Clear(); }
        foreach (var c in clients) c.Dispose();
        if (_accept is not null) { try { await _accept; } catch { /* 关闭路径：循环的退出异常在此消化，就绪异常已由 StartAsync 观察过 */ } }
        _cts.Dispose();
    }
}

/// <summary>
/// 一条已连接的客户端。广播帧与命令回复共用这一条出站流，所以写入必须串行（裁定 R-9b）。
/// </summary>
internal sealed class ConnectedClient : IDisposable
{
    // 有界通道 + 单写任务 = 生产侧（Tray 的广播线程）永不阻塞：TryWrite 立即返回。
    // 满 1024 帧只可能发生在客户端彻底卡死（不读盘）时，DropWrite 丢的是**新帧**，
    // 代价是把甩在身后的慢客户端留在身后，而不是让 Tray 的广播线程跟着挂住
    // （spec §9 明令长耗时/背压不许变成卡顿，Task 15 的轮询会持续往这条流里压帧）。
    private readonly Channel<Frame> _out = Channel.CreateBounded<Frame>(
        new BoundedChannelOptions(1024) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
    private readonly CancellationTokenSource _cts = new();
    private readonly StreamWriter _w;
    private readonly Task _drain;
    private int _disposed;
    public StreamReader Reader { get; }

    public ConnectedClient(NamedPipeServerStream pipe)
    {
        _w = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
        Reader = new StreamReader(pipe, new UTF8Encoding(false));
        _drain = Task.Run(() => DrainAsync(_cts.Token));   // 一条连接只有一个写者，帧永不互踩
    }

    public void Enqueue(Frame f) => _out.Writer.TryWrite(f);

    private async Task DrainAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var f in _out.Reader.ReadAllAsync(ct))
            {
                await _w.WriteAsync(FrameCodec.Encode(f));
                await _w.FlushAsync(ct);   // 一帧一行立刻上线，客户端不必等缓冲区满
            }
        }
        catch { /* 客户端断了或管道被 Dispose：这条连接的写入生命周期到此为止（Dispose 是唯一入口，不静默丢连接） */ }
    }

    public void Dispose()
    {
        // 幂等：DisposeAsync 与 Pump 的 finally 都会调到这里（同一个连接可能被摘两次）。
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _cts.Cancel();
        _out.Writer.TryComplete();
        try { _w.Dispose(); Reader.Dispose(); } catch { }   // 关掉已断的管道会抛 IOException，属正常收尾
        _cts.Dispose();
    }
}
