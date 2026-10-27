using ClashAcry.Contracts;
using ClashAcry.Core;
using System.Diagnostics;
using System.IO;

namespace ClashAcry.UI.Services;

/// <summary>
/// 托盘命令失败的唯一异常类型。<see cref="Message"/> 直接来自 <c>Frame.Error</c>（Tray 侧的原文案），
/// UI 把它当行内文案展示，不弹阻塞框（spec §9）。
/// </summary>
public sealed class TrayCommandException(string message) : Exception(message);

/// <summary>
/// UI ↔ Tray 的唯一通道（spec §3：内核生命周期全在 Tray，UI 只会发命令、读回包）。
/// 三条纪律：
/// ① <see cref="SendAsync"/> 也必须取回包——fire-and-forget 不算成功（禁令 3），
///    "改动已生效"这类文案只允许由 <see cref="RequestAsync{T}"/> 的回包驱动；
/// ② 失败统一折成 <see cref="TrayCommandException"/>，连接中断和命令被拒对调用方是同一种"要告诉用户"；
/// ③ 起进程只允许起 <c>ClashAcry.Tray.exe</c>，绝不起 mihomo。
/// </summary>
public sealed class TrayLink : IAsyncDisposable
{
    private readonly PipeClient _client = new();
    private readonly CancellationTokenSource _ct = new();

    public HandshakeReply? LastHandshake { get; private set; }

    /// <summary>
    /// 让缓存的握手作废：连接一旦断过，上一世的 controller 地址与版本号就不再是事实。
    /// 只有 <see cref="HandshakeAsync"/> 会把 <see cref="LastHandshake"/> 置 null，所以调用方在失败分支里
    /// 必须显式作废一次——否则 <c>RefreshAsync</c> 的「握过手就跳过探活与握手」这条短路会永久成立，
    /// Tray 重启后界面一直拿旧握手读数（版本号停在更新前、controller 打向没人听的端口）。
    /// </summary>
    public void InvalidateHandshake() => LastHandshake = null;

    /// <summary>测速目标 URL，由 handshake 填；Task 15 的测速与 Task 14 的输入框都读这里。</summary>
    public string DelayUrl => LastHandshake?.DelayTestUrl ?? string.Empty;

    /// <summary>转发管道事件（core.state / core.update.progress / settings.changed…）。
    /// PipeServer 侧那条循环**有意不吞订阅者异常**，所以渲染相关的订阅必须在自己那侧吃掉。</summary>
    public event Action<Frame>? Event
    {
        add => _client.Event += value;
        remove => _client.Event -= value;
    }

    /// <summary>
    /// Tray 不在就把它拉起来（这是 UI 唯一允许的跨进程 <c>Process.Start</c>，且只起 Tray）。
    /// 探活预算：先 500 ms 试已在的；拉起后再给 3 s 等它的管道起来；仍失败 = 假，由调用方出横幅。
    /// </summary>
    public async Task<bool> EnsureTrayRunningAsync()
    {
        if (await _client.WaitUpAsync(TimeSpan.FromMilliseconds(500))) return true;

        var trayExe = Path.Combine(AppContext.BaseDirectory, "ClashAcry.Tray.exe");
        if (File.Exists(trayExe))
        {
            try { Process.Start(new ProcessStartInfo(trayExe) { UseShellExecute = true }); }
            catch (Exception) { /* 起不来统一由下面的探活判定，不在这里发明第二种失败表达 */ }
        }
        return await _client.WaitUpAsync(TimeSpan.FromSeconds(3));
    }

    /// <summary>
    /// 只探活、**不拉进程**（拉 Tray 仍归外壳的 <see cref="EnsureTrayRunningAsync"/>，spec §3）。
    /// 给轮询用：托盘可能是后来才起来的，界面自己不该把一个 bool 契约变成异常。
    /// </summary>
    public Task<bool> WaitServerUpAsync(TimeSpan budget) => _client.WaitUpAsync(budget);

    /// <summary>握手失败回 null（不抛）：这是"界面还开得住、但数据不是内核当前状态"的降级入口。</summary>
    public async Task<HandshakeReply?> HandshakeAsync()
    {
        try { LastHandshake = await RequestAsync<HandshakeReply>(CmdTypes.Handshake); }
        catch (TrayCommandException) { LastHandshake = null; }
        return LastHandshake;
    }

    public async Task<T?> RequestAsync<T>(string cmd, object? payload = null)
    {
        Frame? reply;
        try { reply = await _client.RequestAsync(cmd, payload, _ct.Token); }
        // PipeClient 在未连接时是 InvalidOperationException（不是 IOException）：轮询在「托盘根本没起来」
        // 时每 2 s 都会撞上它。不折进 TrayCommandException 就等于让界面弹一条裸栈。
        catch (InvalidOperationException ex) { throw new TrayCommandException($"与托盘宿主未连接：{ex.Message}"); }
        catch (IOException ex) { throw new TrayCommandException($"与托盘的连接中断：{ex.Message}"); }
        catch (OperationCanceledException) { throw new TrayCommandException("请求已取消"); }

        if (reply is null) throw new TrayCommandException("托盘无应答");
        if (reply.Error is not null) throw new TrayCommandException(reply.Error);
        return reply.Payload is null ? default : JsonUtil.Read<T>(reply.Payload);
    }

    /// <summary>丢弃回包但**仍然等它**：语义是"我要知道这条成功了没有"，不是"发出去就算"。</summary>
    public async Task<bool> SendAsync(string cmd, object? payload = null)
    {
        await RequestAsync<bool>(cmd, payload);
        return true;
    }

    public ValueTask DisposeAsync()
    {
        _ct.Cancel();
        return _client.DisposeAsync();
    }
}
