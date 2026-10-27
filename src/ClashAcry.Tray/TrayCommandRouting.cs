using System.Text.Json;
using ClashAcry.Contracts;
using ClashAcry.Core;

namespace ClashAcry.Tray;

/// <summary>
/// 命令 → 动作的唯一映射点（spec §3：内核生命周期只归 Tray，而 Tray 侧这一切都从这里进）。
/// 不依赖托盘窗口句柄、不依赖 XAML，因此全部命令可在无交互环境单测（Core.Tests 的 TrayCommandRoutingTests）。
/// 约定：业务失败（导入源站挂了、zip 解不出、更新器没落地）走 ImportReply.Reason / UpdateResult 回包；
/// 请求本身不合法（未知命令、缺参数、端口越界）走 Frame.Error。UI 靠这个区分「可展示的结果」和「管道故障」。
/// 三条硬规矩（AGENTS.md / spec §3 §4.1 §9）：
/// ① core.start/stop/reload/update 全部经 <see cref="CoreManager"/> 的生命周期锁——本类绝不出现
///    <c>Process.Start</c> 或 <c>File.Replace</c>；
/// ② <c>settings.update</c> 与 <c>settings.changed</c> 共用 <see cref="SettingsSnapshot"/> 一种形状，
///    回包里永不含 <c>secret</c>/<c>core_path</c>（R-10f，secret 只在 handshake 下发）；
/// ③ mixed 地址一律取 settings.MixedPort，不许硬编码 7897。
/// </summary>
public sealed class TrayCommandRouting
{
    private readonly CoreManager _core;
    private readonly SettingsStore _store;
    private readonly MihomoClient _client;
    private readonly AppPaths _paths;
    private readonly HttpClient _http;

    /// 裁定 R-10a：CoreUpdater/ReleaseClient 是 Task 11 的类型，这条缝做成委托——
    /// 路由与测试零改动，Task 11 只替换 TrayHost 装配处的那一行。
    private readonly Func<IProgress<UpdateProgress>?, CancellationToken, Task<UpdateResult>> _updateCore;
    private readonly PipeServer _pipe;
    private readonly Action _focusUi;

    public TrayCommandRouting(CoreManager core, SettingsStore store, MihomoClient client,
        AppPaths paths, HttpClient http,
        Func<IProgress<UpdateProgress>?, CancellationToken, Task<UpdateResult>> updateCore,
        PipeServer pipe, Action focusUi)
    {
        _core = core; _store = store; _client = client; _paths = paths;
        _http = http; _updateCore = updateCore; _pipe = pipe; _focusUi = focusUi;
    }

    public async Task<Frame?> Handle(Frame f)
    {
        try
        {
            object? result = f.Type switch
            {
                CmdTypes.Handshake => Handshake(),
                CmdTypes.CoreStart => await _core.StartAsync(default),
                CmdTypes.CoreStop => await StopAndCleanProxyAsync(),
                CmdTypes.CoreReload => await ReloadOrStartAsync(),
                CmdTypes.ModeSet => await SetModeAsync(Read<ModeRequest>(f).Mode),
                CmdTypes.SysProxySet => ApplySysProxy(Read<SysProxyRequest>(f).Enabled),
                CmdTypes.SettingsUpdate => ApplySettings(Read<SettingsPatch>(f)),
                CmdTypes.SettingsGet => Snapshot(),        // 只读取快照，绝不落盘、绝不广播（R-14a）
                CmdTypes.UiFocus => FocusUi(),
                CmdTypes.CoreUpdate => await UpdateCoreAsync(),
                CmdTypes.SubImport => await ImportAsync(f),
                _ => throw new InvalidOperationException($"unknown command: {f.Type}")
            };
            return f with { Kind = "reply", ReplyTo = f.Id, Error = null, Payload = JsonUtil.Element(result!) };
        }
        catch (Exception ex)
        {
            // 路由不许把异常抛给 PipeServer（那里只会当成连接故障处理）；请求级的失败一律回帧。
            return f with { Kind = "reply", ReplyTo = f.Id, Error = ex.Message, Payload = null };
        }
    }

    private static T Read<T>(Frame f) =>
        f.Payload is null ? throw new InvalidOperationException($"{f.Type}: 缺少请求参数") : JsonUtil.Read<T>(f.Payload);

    // 裁定 R-10e：ui.focus 的动作**只**是「交给注入的 focusUi」（Tray 宿主里 = UI 不在就起进程，已在就不动）。
    // 这里绝不回头调 Handle——路由的 ui.focus 分支就是 focusUi，回头一次就是 Handle→FocusUi→Handle 爆栈。
    private bool FocusUi() { _focusUi(); return true; }

    /// secret 唯一的下发处（spec §4.1 红线）：除本回包外，任何帧都不许把它序列化上线。
    private HandshakeReply Handshake()
    {
        var s = _store.Load();
        return new HandshakeReply(_core.State, s.ControllerHost, s.ControllerPort, s.Secret,
                                  s.MixedPort, s.CoreVersion, s.DelayTestUrl);
    }

    private async Task<bool> StopAndCleanProxyAsync()
    {
        var ok = await _core.StopAsync(default);   // R-10b：StopAsync 的声明改回 Task<bool>，这里才取得到值
        // 停止后进程已不在；用 RunningProcessId 作真判据，绝不写死 false（重启竞态下会误清别人的代理）
        SystemProxy.CleanupIfStale(_store.Load().MixedPort, () => _core.RunningProcessId is not null);
        _pipe.Broadcast(EventTypes.SettingsChanged, Snapshot());
        return ok;
    }

    private async Task<bool> ReloadOrStartAsync()
    {
        if (_core.State is CoreState.Running) { await _core.ReloadAsync(default); return true; }
        return await _core.StartAsync(default);
    }

    private async Task<bool> SetModeAsync(string mode)
    {
        await _client.SetModeAsync(mode, default);
        return true;                                    // 不回显本地值：UI 重读 GET /proxies（spec §7）
    }

    private SettingsSnapshot Snapshot()
    {
        var s = _store.Load();
        return new SettingsSnapshot(SystemProxy.IsEnabled(), s.MixedPort, s.DelayTestUrl, s.CoreVersion);
    }

    private bool ApplySysProxy(bool enabled)
    {
        var port = _store.Load().MixedPort;             // 硬规矩③：地址来自 settings，不写死 7897
        var server = $"127.0.0.1:{port}";
        if (enabled)
        {
            // R-20b：先把用户此刻的两键原值留在 settings.json，再动手覆盖。
            // 只在「当前值不是我们自己的串」时建档：开→关（还原失败）→再开 这条路径上，
            // 此刻的值已经是上一轮我们写的，再存一次就是把自己伪装成"原值"，那份真原值就永久丢了。
            var before = SystemProxy.Capture();
            SystemProxy.Enable(server, SystemProxy.DefaultBypass);
            if (!string.Equals(before.Server, server, StringComparison.OrdinalIgnoreCase))
                _store.Update(s => s with { SysProxyBackup = before });
        }
        else
        {
            // Disable 只翻开关，绝不抹掉 ProxyServer（R-7c）；载荷只在还原条件成立时才回去。
            SystemProxy.Disable();
            var backup = _store.Load().SysProxyBackup;
            if (backup is not null &&
                SystemProxy.Restore(backup, server, SystemProxy.DefaultBypass))
                _store.Update(s => s with { SysProxyBackup = null });
        }
        _pipe.Broadcast(EventTypes.SettingsChanged, Snapshot());   // 事件形状只有一种
        return enabled;
    }

    private SettingsSnapshot ApplySettings(SettingsPatch p)
    {
        // 边界校验（spec §5）：只有**这次真的带上来的**字段才校验，坏请求当场拒、绝不落盘。
        // 派发样例那句 `(p.MixedPort ?? 0) is var mp && (mp < 1 || …)` 将 null 折成 0 后一并判坏，
        // 后果是「只改 delay_test_url」的合法补丁也被拒（实测见报告），这里按字段各判各的。
        if (p.MixedPort is { } mp && (mp < 1 || mp > 65535)) throw new InvalidOperationException("settings.update: 端口需在 1-65535");
        if (p.ControllerPort is { } cp && (cp < 1 || cp > 65535)) throw new InvalidOperationException("settings.update: 端口需在 1-65535");

        _store.Update(s => s with
        {
            MixedPort = p.MixedPort ?? s.MixedPort,
            ControllerPort = p.ControllerPort ?? s.ControllerPort,
            DelayTestUrl = string.IsNullOrWhiteSpace(p.DelayTestUrl) ? s.DelayTestUrl : p.DelayTestUrl
        });
        _pipe.Broadcast(EventTypes.SettingsChanged, Snapshot());
        // 回包是 SettingsSnapshot 而不是 Settings：Settings 里有 secret / core_path，
        // 把它们序列化进帧就是第二条 secret 泄露面（R-10f，spec §4.1 红线）。
        return Snapshot();
    }

    private async Task<UpdateResult> UpdateCoreAsync()
    {
        // spec §9「长耗时操作必须有进行中反馈」：进度→结果这三条是 UI 的契约，不随 Task 11 的实现挪动。
        var progress = new Progress<UpdateProgress>(p => { if (_pipe.ClientCount > 0) _pipe.Broadcast(EventTypes.UpdateProgress, p); });
        var res = await _updateCore(progress, default);   // Task 11 把装配处换成真 CoreUpdater，路由不动
        if (res is UpdateResult.Updated)                // 版本号只在更新成功时重写，UI 收到 result 后重发 handshake 取新值
        {
            // 真机端到端（Task 11 Step 6）踩到的：换核成功后内核**不一定在跑**——换之前没跑就不起，起了也可能探活失败，
            // 这条裸调用于是在内核已经换好的当口把 "connection refused" 抛穿到回帧，用户看到的是一句 socket 错误，
            // 而下面的 UpdateResult 事件也一起丢了。读不到版本号是常态不是故障：CoreVersion 只是展示值，
            // 内核真跑起来后 handshake 会带真版本。
            try
            {
                var v = await _client.GetVersionAsync(default);
                if (v is not null) _store.Update(s => s with { CoreVersion = v.Version });
            }
            catch (Exception) { /* 内核没在跑：见上 */ }
        }
        if (_pipe.ClientCount > 0) _pipe.Broadcast(EventTypes.UpdateResult, res);
        return res;
    }

    private async Task<ImportReply> ImportAsync(Frame f)
    {
        // 裁定 R-10g：缺请求与「请求里两个字段都空」归成同一句文案，且必须含 "url"（UI 原样行内展示）。
        if (f.Payload is null) throw new InvalidOperationException("sub.import: 需要 url 或 path");
        var req = Read<ImportRequest>(f);
        if (req.Url is null && req.Path is null) throw new InvalidOperationException("sub.import: 需要 url 或 path");
        var imp = new SubscriptionImport(_paths, _store);
        // ImportFromUrl 是同步方法（内部同步取字节）：放到线程池上跑，别占住管道的 Pump 线程。
        var r = req.Path is not null ? imp.ImportFromFile(req.Path)
                                     : await Task.Run(() => imp.ImportFromUrl(req.Url!, _http, default));
        if (r.ReasonIfFailed.Length == 0) _store.Update(s => s with { LastSubscription = req.Url ?? req.Path });
        // 失败语义（Task 5 / spec §9）：ConfigFile 仍是旧配置路径，永不清空配置；
        // 业务失败走 Reason 回包而不是 Frame.Error——UI 要能区分「可展示的结果」与「管道故障」。
        return new ImportReply(r.ConfigFile, r.ReasonIfFailed.Length == 0 ? null : r.ReasonIfFailed);
    }
}
