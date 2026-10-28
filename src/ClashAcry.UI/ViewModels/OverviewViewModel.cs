using System.Net.Http;
using ClashAcry.Contracts;
using ClashAcry.Core;
using ClashAcry.UI.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;   // DispatcherTimer 挂在这个命名空间下，不在 Microsoft.UI.Dispatching 里

namespace ClashAcry.UI.ViewModels;

/// <summary>
/// 外壳级状态机（概览页与设置页共用同一个实例，由 <c>MainWindow</c> 持有——R-14d：两个页面各自轮询
/// 会把同一条管道压满，还会让「系统代理 / 测速地址」在两页显示成两个值）。
/// 四条规矩都来自硬约束，不是风格问题：
/// ① **跨进程只走 <see cref="TrayLink"/>**：界面绝不 spawn/kill mihomo，也绝不自己写 settings.json（spec §3、§7）；
/// ② **值真变了才发 <see cref="Changed"/>**：2 s 一轮的轮询不得留下可见痕迹（AGENTS.md 红线）；
/// ③ **点下去不等于成功**：内核状态只由 <c>core.state</c> 事件与握手驱动，界面不做乐观翻转（spec §3 禁令 3）；
/// ④ **长操作期间暂停轮询**（R-14c）：PipeServer 每条连接的请求是**串行**处理的，一条分钟级的
///    <c>core.update</c> 会把同一条连接后面的请求全堵住——轮询照打等于堆一串没人应答的 await。
///    进度与结果走广播（另一条写路径），不受那次队头阻塞影响，所以「进行中反馈」不会断。
/// 所有展示值都是**派生只读属性**：没有第二份状态可以变旧，也就没有「忘了同步某一处」这种 bug。
/// </summary>
public sealed class OverviewViewModel : IDisposable
{
    private static readonly string[] KnownModes = ["rule", "global", "direct"];

    private readonly TrayLink _link;
    private readonly DispatcherQueue _queue;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(3) };
    private DispatcherTimer? _timer;
    private MihomoClient? _rest;
    private HandshakeReply? _restSource;              // _rest 是按哪次握手建的：地址或 secret 变了必须重建

    private CoreState? _state;                        // null = 从没握手成功过
    private int _pending;                             // 在飞的跨进程操作数
    private bool _refreshing;
    private bool _faulted;                            // 只在「好 → 坏」翻转那一刻写横幅，坏着时每 2 s 再喊就是噪音
    private bool _sysProxy;
    private string _mode = "", _coreVersion = "未知", _delayUrl = "", _logText = "", _errorText = "";
    private string _coreFault = "";                       // 内核故障那一格：见 <see cref="ErrorText"/>
    private int _updatePct = -1;

    public OverviewViewModel(TrayLink link, DispatcherQueue queue)
    {
        _link = link; _queue = queue;
        _link.Event += OnFrame;
    }

    /// <summary>
    /// 轮询周期。一轮失败就是一轮够不着托盘；连续 <see cref="LinkGraceRounds"/> 轮都够不着，
    /// 界面把窗口收掉（<see cref="TrayLost"/>）。
    /// </summary>
    private const int PollSeconds = 2;

    /// <summary>
    /// 断连宽限的轮数（≈6 s）。这不是"多给界面一点命"，而是给**托盘自己重启**留的口子：
    /// 托盘可能只是被更新内核换了一次身，几秒内管道就回来，界面续上就是续上（OverviewTests 里
    /// <c>TrayRestarts_WithinGraceUiKeepsTalking</c> 钉的正是这一条）。超过它还没回来，就认定宿主没了。
    /// </summary>
    private const int LinkGraceRounds = 3;

    private int _linkDownRounds;

    /// <summary>
    /// 托盘在宽限内再也够不着了。<see cref="MainWindow"/> 收到就把窗口关掉——<c>App</c> 那条
    /// 「关窗即退」会带走进程。与 R-20a（托盘「退出」关掉界面）合起来是同一句话：**UI 与 Tray 同生死**，
    /// 不该留下一个横幅念着"连接中断"、控制全灰、其实什么都做不了的窗口（用户裁定 2026-10-28）。
    /// 内核崩溃**不走这里**：那时托盘还在，用户要的是在同一个界面里把它重新按起来。
    /// </summary>
    public event Action? TrayLost;

    /// <summary>任何一个真值变了才发；页面收到后把所有判据重刷一遍（页面的写入自己再挡一次同值）。</summary>
    public event Action? Changed;

    public bool TrayConnected => _state is not null;
    public bool Busy => Volatile.Read(ref _pending) > 0;
    public string CoreStateText => _state is null ? "不可用" : StateTextOf(_state.Value);

    /// <summary>连不上或正有操作在飞时开关就是灰的——控制不了的东西不许做出「能控制」的样子。</summary>
    public bool CoreToggleEnabled => _state is not null && !Busy;
    public bool CoreOn => _state is CoreState.Running;
    public bool ModeEnabled => _state is CoreState.Running && !Busy;
    public bool SysProxyEnabled => _state is not null && !Busy;
    public bool SysProxyOn => _sysProxy;
    public string SysProxyText => _sysProxy ? "已开启" : "未开启";
    public string Mode => _mode;
    public int UpdatePct => _updatePct;
    public bool UpdateBarVisible => _updatePct >= 0;
    public string UpdateButtonText => _updatePct < 0 ? "更新内核" : $"更新内核 {_updatePct}%";
    public bool UpdateEnabled => _state is not null && !Busy;
    public string CoreVersion => _coreVersion;
    public string LogText => _logText;

    /// <summary>
    /// 测速地址：优先 <c>settings.get</c> 的快照（那是引擎侧事实源），快照还没回来时退回握手带的值。
    /// 退回不是兜底偏好——节点页可能比第一轮快照更早打开，读空串就等于发一个 <c>?url=</c> 的废请求。
    /// </summary>
    public string DelayUrl => _delayUrl.Length > 0 ? _delayUrl : _restSource?.DelayTestUrl ?? "";

    /// <summary>
    /// 按最近一次成功握手建出的 controller 客户端（未握手成功时为 null）。
    /// 节点页要直连 REST 读 <c>/proxies</c>，客户端在这里出、secret 就只有一处去处（R-14d 同一理由）。
    /// </summary>
    public MihomoClient? Rest => _rest;

    /// <summary>
    /// 横幅上有两格话，来源与销账时机都不同（第六轮拆开）：
    /// ① 连接那一格（<c>_errorText</c>）说"连不上托盘"，由下一次成功的轮询收起；
    /// ② 内核那一格（<c>_coreFault</c>）说"崩溃原因"，留到状态离开 <c>Crashed</c> 才销。
    /// 连接故障优先：托盘都够不着时，"配置解析失败"这种上一世的内核消息没有意义。
    /// 拆开之前它们是同一格，于是第五轮那条「状态一变就作废握手」把崩溃原因压成了一次闪光
    /// ——连接一直好着的话，下一轮轮询的 <c>SetFault("")</c> 就在用户读完之前把它抹掉（锁
    /// <c>CoreCrashFault_StaysReadable_UntilCoreStateMoves</c>）。空 = 一切正常。它是横幅唯一的文本来源
    /// （R-14e：否则外壳与页面会各写一份、互相盖掉对方）。</summary>
    public string ErrorText => _errorText.Length > 0 ? _errorText : _coreFault;

    /// <summary>外壳把「托盘宿主未运行」这类自己判定的故障交进来，与轮询故障同一条「第一条说话」规则。</summary>
    public void ReportFault(string message) => SetFault(message);

    /// <summary>当前值不在预设三项里时并进去（v1 只支持三个常用 204 探针，用户手改过 settings 也不能显示空白）。</summary>
    public IReadOnlyList<string> DelayUrlOptions { get; private set; } =
        ["http://www.gstatic.com/generate_204", "https://cp.cloudflare.com/generate_204", "http://www.google.com/generate_204"];

    public static string StateTextOf(CoreState s) => s switch
    {
        CoreState.Stopped => "未运行",
        CoreState.Starting => "启动中…",
        CoreState.Running => "运行中",
        CoreState.Crashed => "异常",
        CoreState.Updating => "更新中…",
        _ => s.ToString(),
    };

    public void Start()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(PollSeconds) };   // spec §6 的轮询周期
        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();
        _ = RefreshAsync();                                                   // 不等第一个周期就先刷一次
    }

    public void Dispose()
    {
        _timer?.Stop();
        _link.Event -= OnFrame;
        _http.Dispose();
    }

    /// <summary>一轮静默刷新：握手（缺时补）→ <c>settings.get</c> 快照 → 内核在跑再读一次 <c>GET /configs</c> 拿模式。</summary>
    public async Task RefreshAsync()
    {
        if (_refreshing || Busy) return;
        _refreshing = true;
        try
        {
            var wasHandshaked = _link.LastHandshake is not null;
            // 还没握手过就先探一次管道（托盘可能是后来才起来的）。这里**只探活不拉进程**——
            // 起 Tray 归外壳那一次（spec §3），轮询里藏一个 Process.Start 会变成每 2 s 试一次拉起。
            if (!wasHandshaked) await _link.WaitServerUpAsync(TimeSpan.FromMilliseconds(500));
            var hs = wasHandshaked ? _link.LastHandshake : await _link.HandshakeAsync();
            if (hs is null) { NoteLinkDown(); Assign(ref _state, (CoreState?)null); return; }
            // 「内核在跑却读不到版本号」的握手不是事实，只是**抓早了**：Tray 把 /version 的真值写进
            // settings 落在 Transition(Running) 之后几毫秒，握手卡进那几毫秒就会拿到一份 null 版本的描述，
            // 而同一状态不会再发第二次事件、轮询又一路复用它——版本号就此钉死在「未知」。
            // 所以这一份只用于本轮读数，不许留下当缓存（下一轮重问，真值落地即止）。
            if (hs.State is CoreState.Running && hs.CoreVersion is null) _link.InvalidateHandshake();

            EnsureRest(hs);
            // 「还没有任何状态」才用手串打底——不是「本轮没握手过」才打底：外壳在 Loaded 里已经握过一次，
            // 若按后者判定，_state 会永远停在 null，界面全灰却显示着版本号（Task 14 首轮实测）。
            // 此后状态只由 core.state 事件说：点下去不等于成功（spec §3 禁令 3）。
            if (_state is null) Assign(ref _state, hs.State);
            Assign(ref _coreVersion, hs.CoreVersion ?? "未知");    // 版本只认握手：进度数字绝不冒充版本

            var snap = await _link.RequestAsync<SettingsSnapshot>(CmdTypes.SettingsGet);
            if (snap is not null) ApplySnapshot(snap);

            if (_state is CoreState.Running && _rest is { } rest)
            {
                try { ApplyMode((await rest.GetConfigsAsync(default))?.Mode); }
                // 内核刚崩 / 端口刚放时读不到模式是常态，不是托盘故障：静默留着上一次的值，状态由 core.state 说。
                catch (Exception) { }
            }
            _linkDownRounds = 0;                      // 说得上话就销账：宽限计的是**连续**失败的轮数
            SetFault("");
        }
        catch (TrayCommandException ex)
        {
            SetFault(ex.Message);
            Assign(ref _state, (CoreState?)null);
            // 连接断过 = 缓存的握手不再可信：作废它，下一轮才会重新探活 + 重新握手（TrayLink 那条注释）。
            _link.InvalidateHandshake();
            NoteLinkDown();
        }
        finally { _refreshing = false; }
    }

    /// <summary>
    /// 一轮失败就是一轮够不着托盘；连续到宽限的尽头就把窗口交给 <see cref="TrayLost"/>。
    /// 只由 <see cref="RefreshAsync"/> 的两条**连接级**失败分支调用（握手拿不到、轮询里的命令抛
    /// <see cref="TrayCommandException"/>）；用户动作（开关内核、导入、更新）的失败走
    /// <see cref="RunAsync"/> 那条 catch，只写故障文案、不计入宽限——那种时候宿主好好的，界面更不该退。
    /// 反过来，任何一次说得上话的用户动作也算证据：<see cref="RunAsync"/> 的成功分支会把计数销掉，
    /// 免得"轮询恰好连输三轮、而界面其实一直在正常干活"被当成宿主没了。
    /// </summary>
    private void NoteLinkDown()
    {
        if (++_linkDownRounds < LinkGraceRounds) return;
        TrayLost?.Invoke();
    }

    // ---- 用户动作：只发命令，成不成由事件与重读决定 -----------------------------------------------

    /// <summary>内核开关。参数是开关上的**目标**状态；界面状态不因此改变（spec §3 禁令 3）。</summary>
    public void SetCore(bool on) => Send(on ? CmdTypes.CoreStart : CmdTypes.CoreStop);

    public void RequestMode(string mode)
    {
        if (KnownModes.Contains(mode)) Send(CmdTypes.ModeSet, new ModeRequest(mode));
    }

    public void RequestSysProxy(bool on) => Send(CmdTypes.SysProxySet, new SysProxyRequest(on));

    /// <summary>换测速地址：写走 <c>settings.update</c>，回显以 <c>settings.changed</c> 广播的快照为准。</summary>
    public void RequestDelayUrl(string url)
    {
        if (!string.IsNullOrWhiteSpace(url)) Send(CmdTypes.SettingsUpdate, new SettingsPatch(null, null, url));
    }

    /// <summary>更新内核：点下去立刻进入「0%」，不等第一帧进度（spec §9：禁止无反馈的进行中）。</summary>
    public void StartCoreUpdate()
    {
        if (!CoreToggleEnabled) return;
        Begin();
        Assign(ref _updatePct, 0);
        _ = RunAsync<UpdateResult>(CmdTypes.CoreUpdate, null, FinishUpdate);
    }

    /// <summary>导入区在设置页，但它同样是在飞的长操作：让外壳知道，好把轮询暂停。</summary>
    public void BeginExternalWork() => Begin();

    public void EndExternalWork() => End();

    private void Send(string cmd, object? payload = null)
    {
        if (!CoreToggleEnabled) return;
        Begin();
        _ = RunAsync<bool>(cmd, payload, _ => { });
    }

    private async Task RunAsync<T>(string cmd, object? payload, Action<T> onResult)
    {
        try
        {
            var reply = await _link.RequestAsync<T>(cmd, payload);
            _linkDownRounds = 0;      // 用户动作说得上话 = 宿主在，宽限计数跟着销账（见 NoteLinkDown）
            SetFault("");
            if (reply is not null) onResult(reply);
        }
        catch (TrayCommandException ex)
        {
            SetFault(ex.Message);
            // 在飞的 core.update 被断线打断时，回帧与 core.update.result 广播**两条一起丢**，
            // FinishUpdate 永远等不到——不复位就是按钮停在「更新内核 37%」直到进程重启。
            // spec §9 的进行中反馈必须有终点，终点也包括失败这条。
            if (cmd == CmdTypes.CoreUpdate && _updatePct >= 0) Assign(ref _updatePct, -1);
        }
        finally { if (_pending > 0) End(); }
    }

    // ---- 事件帧 -------------------------------------------------------------------

    private void OnFrame(Frame f)
    {
        // 事件来自管道的读循环线程：渲染必须回 UI 线程；而且订阅者抛出去会掀掉整条连接
        //（PipeClient 有意不吞，见其 ReadLoop），所以这里自己吃掉。
        _queue.TryEnqueue(() =>
        {
            try
            {
                switch (f.Type)
                {
                    case EventTypes.CoreStateChanged:
                        if (JsonUtil.Read<CoreStateChanged>(f.Payload) is not { } e) return;
                        Assign(ref _logText, string.Join("\n", e.StderrTail));
                        Assign(ref _state, e.State);
                        // 状态一变，缓存的握手就不再是事实：版本号只有内核跑起来后 Tray 才回读得到，
                        // controller 地址/secret 也可能随设置改动（v1.1 欠账①「冷装后恒未知」的界面侧）。
                        // 这里只**作废**不重握手——握手仍在轮询那条线上，界面不另开第二个说话入口。
                        _link.InvalidateHandshake();
                        // 内核故障写**自己那一格**，不借连接那格：轮询每 2 s 就会把连接那格清空（见 ErrorText）。
                        // 销账由状态自己负责——Crashed → 别的状态（重启、停止后重开）才算这句话讲完。
                        if (e.State is CoreState.Crashed) Assign(ref _coreFault, e.Reason ?? "内核异常退出");
                        else Assign(ref _coreFault, "");
                        break;
                    case EventTypes.UpdateProgress:
                        // 只认「正在更新」时的进度帧：不在更新时落一帧就把按钮永久停在百分号上。
                        if (_updatePct >= 0 && JsonUtil.Read<UpdateProgress>(f.Payload) is { } p)
                            Assign(ref _updatePct, p.Pct);
                        break;
                    case EventTypes.UpdateResult:
                        if (JsonUtil.Read<UpdateResult>(f.Payload) is { } r) FinishUpdate(r);
                        break;
                    case EventTypes.SettingsChanged:
                        if (JsonUtil.Read<SettingsSnapshot>(f.Payload) is { } snap) ApplySnapshot(snap);
                        break;
                }
            }
            catch (Exception) { /* 一帧渲染失败不该拖垮连接：下一次轮询会把真值读回来 */ }
        });
    }

    // ---- 落地 -------------------------------------------------------------------

    private void ApplySnapshot(SettingsSnapshot snap)
    {
        Assign(ref _sysProxy, snap.SysProxyEnabled);
        if (!string.IsNullOrWhiteSpace(snap.DelayTestUrl))
        {
            if (snap.DelayTestUrl != _delayUrl) Assign(ref _delayUrl, snap.DelayTestUrl);
            if (!DelayUrlOptions.Contains(snap.DelayTestUrl))
            {
                DelayUrlOptions = new[] { snap.DelayTestUrl }.Concat(DelayUrlOptions).ToArray();
                Changed?.Invoke();
            }
        }
    }

    private void ApplyMode(string? mode)
    {
        if (mode is not null && KnownModes.Contains(mode)) Assign(ref _mode, mode);
    }

    private void EnsureRest(HandshakeReply hs)
    {
        if (_restSource == hs && _rest is not null) return;
        // secret 只活在这个客户端对象里：不写日志、不进任何控件文案、不发往非 controller 端点（spec §4.1 红线）。
        _rest = new MihomoClient(_http, hs.Host, hs.Port, hs.Secret);
        _restSource = hs;
    }

    private void FinishUpdate(UpdateResult result)
    {
        // 回帧与 core.update.result 广播是同一件事的两个去处，只认第一次；版本号要靠重新握手才有真值。
        if (_updatePct < 0) return;
        Assign(ref _updatePct, -1);
        _ = RefreshHandshakeAsync();
    }

    private async Task RefreshHandshakeAsync()
    {
        try
        {
            var hs = await _link.HandshakeAsync();       // 它自己吞失败，返回 null 就是还没连上
            if (hs is null) return;
            EnsureRest(hs);
            Assign(ref _state, hs.State);
            Assign(ref _coreVersion, hs.CoreVersion ?? "未知");
        }
        catch (TrayCommandException) { }
    }

    private void Begin() { Interlocked.Increment(ref _pending); Changed?.Invoke(); }
    private void End() { Interlocked.Decrement(ref _pending); Changed?.Invoke(); }

    private void SetFault(string message)
    {
        if (string.IsNullOrEmpty(message))
        {
            if (!_faulted) return;
            Assign(ref _errorText, "");
            _faulted = false;
            return;
        }
        // 第一条故障说话，坏着期间不换措辞：2 s 一轮的轮询会把同一个故障说出五六种句子
        //（先是「未连接」、断线后是「连接中断」），横幅跟着跳等于在用户眼前抖动。
        if (_faulted) return;
        Assign(ref _errorText, message);
        _faulted = true;
    }

    private void Assign<T>(ref T field, T value)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        Changed?.Invoke();
    }
}
