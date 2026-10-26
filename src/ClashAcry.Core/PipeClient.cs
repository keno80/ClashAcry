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
    private readonly Dictionary<string, TaskCompletionSource<Frame>> _pending = new();
    private readonly object _gate = new();
    private int _disposed;   // DisposeAsync 幂等：与 PipeServer 同理，第二次会在已 Dispose 的 CTS/Semaphore 上抛
    public event Action<Frame>? Event;

    public async Task<bool> WaitUpAsync(TimeSpan timeout)
    {
        // 已在连着的连接上再等一次是白等：Task 12 的 TrayLink 会反复探活，
        // 每次重连都会留下上一条 ReadLoop 与旧字段（旧 _cts 再也无人取消）。
        if (_pipe?.IsConnected == true) return true;

        // 先连上再落字段：原样例把 _pipe 赋在 ConnectAsync 之前，一次失败连接会留下半具尸体的字段，
        // 下一次 WaitUpAsync 就在往废管道上写。
        try
        {
            var pipe = new NamedPipeClientStream(".", ContractConstants.PipeName,
                PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync((int)timeout.TotalMilliseconds);
            _pipe = pipe;
            _w = new StreamWriter(_pipe, new UTF8Encoding(false)) { AutoFlush = true };
            _cts = new CancellationTokenSource();
            _ = Task.Run(() => ReadLoop(_cts.Token));
            return true;
        }
        catch { return false; }   // 超时/找不到管道 = Tray 没在跑，由调用方决定横幅还是重试（spec §4.1）
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

    private async Task ReadLoop(CancellationToken ct)
    {
        var reader = new StreamReader(_pipe!, new UTF8Encoding(false));
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
        return ValueTask.CompletedTask;
    }
}
