using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using ClashAcry.Contracts;

namespace ClashAcry.Core;

/// <summary>
/// UI 侧的管道客户端（Task 12 的 TrayLink 就包一层它）。请求—回复靠帧 id 配对，事件走 <see cref="Event"/>。
/// </summary>
public sealed class PipeClient : IAsyncDisposable
{
    private NamedPipeClientStream? _pipe; private StreamWriter? _w; private CancellationTokenSource? _cts;
    private readonly SemaphoreSlim _writeGate = new(1, 1);   // 裁定 R-9d①：并发请求不许把两帧交错进同一条流
    // 评审 I-3：连接路径专用闸门，与 _writeGate **分开**——共用一个的话「写一行」和「重连」会互相咬死
    //（重连要等写、写要等重连）。原写法是先查后改：`IsConnected` 检查与 _pipe/_w/_cts 赋值之间没有同步，
    // Task 12 的 TrayLink 两条路径（轮询 + 用户操作）同时探活时两次都能连上，后一条覆盖前一条的字段，
    // 被覆盖的那条在服务端永久存活（ClientCount 虚高、广播重复投递、往没人读的流里写帧）。
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private readonly Dictionary<string, TaskCompletionSource<Frame>> _pending = new();
    private readonly object _gate = new();
    private int _disposed;   // DisposeAsync 幂等：与 PipeServer 同理，第二次会在已 Dispose 的 CTS/Semaphore 上抛
    public event Action<Frame>? Event;

    public async Task<bool> WaitUpAsync(TimeSpan timeout)
    {
        // 评审 Minor 12：荒谬的 TimeSpan（如 TimeSpan.MaxValue）TotalMilliseconds 转 int 会溢出成负数，
        // 既不是 -1 也过不了 ConnectAsync，最后被下面的 catch 吃成「静默失败」。先夹到
        // [-1, int.MaxValue]：-1 是 ConnectAsync 约定的「无限等待」（Timeout.InfiniteTimeSpan 本值，
        // 其余负数属非法入参，同样落到「无限」——Task 12 的探活给的是正值），上限约 24.9 天。
        var budgetMs = (int)Math.Clamp(timeout.TotalMilliseconds, Timeout.Infinite, int.MaxValue);

        // 外层短路只为省掉一次闸门排队（反复探活的常态是「已经连着」）；真正的判定在闸门内复检。
        if (_pipe?.IsConnected == true) return true;

        var gateTaken = false;
        try
        {
            await _connectGate.WaitAsync();
            gateTaken = true;
            if (_pipe?.IsConnected == true) return true;   // 闸门内复检：并发探活的另一条在这里改口「已经连上了」

            // 先连上再落字段：原样例把 _pipe 赋在 ConnectAsync 之前，一次失败连接会留下半具尸体的字段，
            // 下一次 WaitUpAsync 就在往废管道上写。
            var pipe = new NamedPipeClientStream(".", ContractConstants.PipeName,
                PipeDirection.InOut, PipeOptions.Asynchronous);
            var connected = false;
            try { await pipe.ConnectAsync(budgetMs); connected = true; }
            finally { if (!connected) pipe.Dispose(); }   // 连不上 = Tray 没在跑，别把句柄留在进程里（spec §4.1）

            var deadPipe = _pipe; var deadWriter = _w; var deadCts = _cts;
            _pipe = pipe;
            _w = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
            var loopCts = new CancellationTokenSource();
            _cts = loopCts;
            // 评审 I-3：管道与它的循环令牌都取**局部**交给 ReadLoop；原来 ReadLoop 读的是字段，
            // 于是新管道的帧可能被上一条循环读走（同一字节流上两个 StreamReader 互抢 = 撕帧/丢回复）。
            _ = Task.Run(() => ReadLoop(pipe, loopCts.Token));

            // 走到这里旧字段必是一条**已断开**的连接（还活着的话闸门内复检就返回真了）：不关掉就每次重连
            // 漏一对句柄 + 一条空转循环。旧 _cts 只 Cancel 不 Dispose——那条循环可能还握着这个令牌，
            // 中途 Dispose 会让它抛一个 ReadLoop 的 catch 过滤器不认识的 ObjectDisposedException。
            try { deadCts?.Cancel(); } catch { /* 已取消/已释放 */ }
            try { deadWriter?.Dispose(); } catch { /* 已断的管道关的时候会抛 */ }
            try { deadPipe?.Dispose(); } catch { /* 同上 */ }
            return true;
        }
        // 探活不抛，成败只由返回值说（与原实现一致）：连不上是「Tray 没在跑」这种常态，
        // 由调用方决定横幅还是重试（spec §4.1）；闸门已被 DisposeAsync 释放时的 ObjectDisposedException
        // 也走这里，不会把一个 bool 契约变成异常。
        catch { return false; }
        // 只在真的取到过闸门时归还：拿不到就 Release 会把 SemaphoreSlim 的计数养成 2，闸门自此失效。
        finally { if (gateTaken) _connectGate.Release(); }
    }

    public Task<bool> IsServerUp() => Task.FromResult(_pipe?.IsConnected == true);

    public async Task<Frame?> RequestAsync(string type, object? payload, CancellationToken ct)
    {
        var w = _w;
        if (w is null) throw new InvalidOperationException("pipe is not connected");   // 裁定 R-9d③（原来这里是 _w! 的 NRE）
        var id = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<Frame>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate) _pending[id] = tcs;
        try
        {
            var frame = new Frame("command", type, payload is null ? null : JsonUtil.Element(payload), 0, id, null, null);
            await _writeGate.WaitAsync(ct);
            try { await w.WriteAsync(FrameCodec.Encode(frame)); } finally { _writeGate.Release(); }

            using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
            return await tcs.Task;
        }
        finally { lock (_gate) _pending.Remove(id); }
    }

    private async Task ReadLoop(NamedPipeClientStream pipe, CancellationToken ct)
    {
        var reader = new StreamReader(pipe, new UTF8Encoding(false));   // 参数是局部：本循环只读它自己那条管道
        try
        {
            while (!ct.IsCancellationRequested)
            {
                Frame? f;
                try { f = await FrameCodec.ReadLineAsync(reader, ct); }
                catch (Exception ex) when (ex is IOException or InvalidDataException
                                           or JsonException or OperationCanceledException) { break; }
                if (f is null) break;   // 干净 EOF：服务端走了
                if (f.Kind == "reply" && f.ReplyTo is not null)
                {
                    TaskCompletionSource<Frame>? tcs;
                    lock (_gate) { _pending.TryGetValue(f.ReplyTo, out tcs); if (tcs is not null) _pending.Remove(f.ReplyTo); }
                    tcs?.TrySetResult(f);
                }
                else Event?.Invoke(f);   // 订阅者异常会掀掉这条连接（有意不吞）：Task 12 的 TrayLink 要在自己那侧吃掉渲染异常
            }
        }
        finally
        {
            // 裁定 R-9d②：流一断，在飞的请求立刻失败。留着不办等于 UI 挂到自己 ct 的超时——
            // spec §9 明令长耗时/异常必须有反馈，静默卡死最糟。
            TaskCompletionSource<Frame>[] inflight;
            lock (_gate) { inflight = _pending.Values.ToArray(); _pending.Clear(); }
            foreach (var tcs in inflight) tcs.TrySetException(new IOException("pipe closed"));
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return ValueTask.CompletedTask;
        _cts?.Cancel();
        try { _w?.Dispose(); } catch { /* 已断的管道关的时候会抛，收尾不需要它 */ }
        // _w.Dispose() 连带关掉底层管道句柄，ReadLoop 因此以 EOF/异常退出并办完 pending 那段 finally（R-9d②）。
        _pipe?.Dispose();
        _cts?.Dispose();
        _writeGate.Dispose();
        _connectGate.Dispose();
        return ValueTask.CompletedTask;
    }
}
