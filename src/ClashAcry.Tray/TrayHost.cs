using System.Diagnostics;
using ClashAcry.Contracts;
using ClashAcry.Core;

namespace ClashAcry.Tray;

/// <summary>
/// Tray 宿主：装配 Core（状态机 + 管道服务端 + 命令路由）并挂上纯 Win32 托盘。
/// 这是 UI 与内核之间的唯一通路——UI 侧永远走命名管道，内核进程只由 <see cref="CoreManager"/> 起停（spec §3）。
/// 启动顺序照计划 Task 10 Step 5：目录 → 设置 → 悬挂代理清理 → 内置内核铺设 → 装配 → 托盘 → 消息循环。
/// v1 语义：**内核不自动启动**（可预期的显式启停），宿主起来只是把控制通道与托盘准备好。
/// </summary>
public sealed class TrayHost
{
    private readonly bool _noTray;
    private AppPaths _paths = null!;
    private SettingsStore _store = null!;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    /// <summary>
    /// 更新通路单独一个 client：内核包是 10 MB 量级的**整包下载**，30 s 的 REST 超时会在慢link上
    /// 把正常的下载掐死成 DownloadFailed。不复用 _http 还有一条好处——两边的默认头互不污染（R-6a 纪律）。
    /// 真机订正：这里的 5 min **只兜到响应头**——ReleaseClient 用 ResponseHeadersRead 发请求，正文读取不受
    /// HttpClient.Timeout 约束（实测 22.5 MB 的包在本机以 ~42 KB/s 跑了 538 s 也没被掐）。
    /// 下载的真正护栏是 <see cref="ReleaseClient.InactivityBudget"/>：静默 30 s 才死，慢但活着就一直跑完。
    /// </summary>
    private readonly HttpClient _updateHttp = new() { Timeout = TimeSpan.FromMinutes(5) };
    public CoreManager Core { get; private set; } = null!;
    public PipeServer Pipe { get; private set; } = null!;
    public TrayCommandRouting Routing { get; private set; } = null!;
    private Win32NotifyIcon? _icon;
    private int _quitting;                                    // R-10j①：菜单连点 / Ctrl+C 与「退出」各来一次也只走一遍

    public TrayHost(bool noTray = false) => _noTray = noTray;

    public async Task RunAsync(CancellationToken ct)
    {
        _paths = AppPaths.Default(); _paths.EnsureCreated();
        _store = new SettingsStore(_paths);
        var s = _store.Load();

        SystemProxy.CleanupIfStale(s.MixedPort, coreAlive: () => false);       // 第 2 层守卫：冷启动专治上次被强杀
        if (!File.Exists(_paths.CoreExecutable))                               // 第 3 步：内置内核只在缺失时铺，不覆盖在线更新结果
        {
            var bundled = Path.Combine(AppContext.BaseDirectory, "core", "mihomo.exe");
            if (File.Exists(bundled)) File.Copy(bundled, _paths.CoreExecutable);
            File.WriteAllText(_paths.CoreVersionFile, "bundled");
        }

        var client = new MihomoClient(_http, s.ControllerHost, s.ControllerPort, s.Secret);
        var probe = new CoreProbe(client);
        Core = new CoreManager(_paths, _store, client, probe);                  // 默认 launchFactory：mihomo -d <runtime>，Tray 侧不自己拼参数
        // Task 11：更新器只交出「怎么换文件」这段回调，停核/换文件/起核与 Updating 状态迁移全在
        // CoreManager.SwapExecutableAsync 的生命周期锁里做（spec §3 禁令 1：更新器不碰进程）。
        var updater = new CoreUpdater(_paths, new ReleaseClient(_updateHttp));
        Pipe = new PipeServer(f => Routing.Handle(f));
        Routing = new TrayCommandRouting(Core, _store, client, _paths, _http,
            // 裁定 R-10a 的落点：这里换成真更新器，路由与测试零改动。
            (p, ct2) => updater.InstallLatestAsync(p, Core.SwapExecutableAsync, ct2),
            Pipe, FocusUi);
        Core.StateChanged += e => Pipe.Broadcast(EventTypes.CoreStateChanged, e);
        await Pipe.StartAsync(ct);                                              // 常驻任务：不能 await 到底

        // 裁定 R-10j②：accept 循环的死因必须有去处，且**不许自动退出**——控制通道断了就把正在跑的内核
        // 一起杀掉，比留着更糟（UI 连不上而内核还在代理流量，是更难发现的那种坏）。
        _ = Pipe.Completion.ContinueWith(_ => _icon?.SetTip("ClashAcry · 服务异常，请重启托盘"),
            TaskContinuationOptions.OnlyOnFaulted);

        if (!_noTray)
        {
            _icon = new Win32NotifyIcon(Path.Combine(AppContext.BaseDirectory, "app.ico"), "ClashAcry");
            // 裁定 R-10e + Step 5 硬要求：五条菜单动作**全部**经 Routing.Handle——菜单回调直接碰内核/注册表/
            // Process.Start 的话，Task 10 的路由测试就覆盖不到托盘这条真实入口（尤其 item 1：样例写的是
            // 直接 FocusUi，R-10e 明确改成走 ui.focus 分支，因为「起 UI 是 Tray 唯一允许的跨进程 Start」，
            // 它同样该在被测的那段代码里）。路由的 ui.focus 分支调回 FocusUi，函数体不再回头调 Handle。
            _icon.MenuItem(1, "打开界面", () => _ = Routing.Handle(Cmd(CmdTypes.UiFocus)));
            _icon.MenuItem(3, "启动内核", () => _ = Routing.Handle(Cmd(CmdTypes.CoreStart)));
            _icon.MenuItem(4, "停止内核", () => _ = Routing.Handle(Cmd(CmdTypes.CoreStop)));
            _icon.MenuItem(5, "切换系统代理", () => _ = Routing.Handle(
                Cmd(CmdTypes.SysProxySet, new SysProxyRequest(!SystemProxy.IsEnabled()))));
            _icon.MenuItem(7, "退出", () => { _ = ShutdownAsync(); });

            _ = Task.Run(() => PumpTooltip(ct));                                 // tooltip 每 2s 由状态刷新，不额外探活
            MessageLoop(ct);                                                     // 托盘回调只在 GetMessageW 循环里被派发
            return;
        }

        // --no-tray（FlaUI/CI 无交互跑宿主）：不建窗口、不建图标，只把控制通道留着，等命令。
        // 没有托盘回调要派发，所以不进消息循环；进程活着靠这条等待。
        try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { }
        await ShutdownAsync();
    }

    private static Frame Cmd(string type, object? payload = null) =>
        new("command", type, payload is null ? null : JsonUtil.Element(payload), 0, Guid.NewGuid().ToString("N"), null, null);

    /// <summary>
    /// 裁定 R-10e：这里**绝不**回头调 Routing.Handle(UiFocus)——路由的 ui.focus 分支就是调本方法，
    /// 回头一次就是 Handle→FocusUi→Handle 爆栈。起 UI 是 Tray 唯一允许的跨进程 Start（spec §3 白名单第二条）；
    /// UI 已在就直接不动（FocusUi 不做 activate，v1 先保证「只有一个 UI 进程」）。
    /// </summary>
    private void FocusUi()
    {
        if (Process.GetProcessesByName("ClashAcry.UI").Length == 0)
            Process.Start(new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "ClashAcry.UI.exe")) { UseShellExecute = true });
    }

    /// <summary>
    /// 退出钩子（R-10j①）：清代理 → 停内核 → 关界面 → 关掉管道服务并**观察它的死因** → Exit。
    /// `Pipe.DisposeAsync()` 只调一次（Task 9 的遗留 Minor：第二次会再抛同一个异常），
    /// 且必须 await——不 await 就永远看不见「就绪之后 accept 循环死了」那条唯一去处（评审 I-1）。
    /// </summary>
    public async Task ShutdownAsync()
    {
        if (Interlocked.Exchange(ref _quitting, 1) != 0) return;

        _icon?.Dispose();                                                       // NIM_DELETE 先走：人最先关心的就是图标还在不在
        await Routing.Handle(Cmd(CmdTypes.SysProxySet, new SysProxyRequest(false)));   // 复用路由：托盘退出与 UI 关代理走同一段代码
        await Core.StopAsync(default);
        CloseUiWindows();                                                       // 裁定 R-20a：托盘是宿主，「退出」= 整个应用走
        try { await Pipe.DisposeAsync(); }
        catch (Exception) { /* 管道服务此前已故障：Completion 的续接已经把 tooltip 写成「服务异常」，这里不重复发明日志（spec/计划里没有 Tray 日志这一项） */ }
        Environment.Exit(0);
    }

    /// <summary>
    /// 裁定 R-20a（用户实测提出，2026-10-27）：托盘「退出」之后界面还留在屏幕上——它只是显示
    /// 「与托盘的连接中断」加一格置灰，用户眼里这是"退没退干净"，不是降级可用状态。
    /// 关掉的方式是 <c>CloseMainWindow()</c>（= WM_CLOSE），由 UI 自己的「关窗即退」（spec §3）把进程带走：
    /// 真机实测 <c>MainWindowHandle</c> 有值、返回 true、3 s 内进程消失。**绝不 <c>Kill()</c>**——
    /// 强杀会在退出钩子里吞掉界面自己的未处理异常落盘（App.OnUnhandled），也可能连带杀掉 UI.Tests 的 FlaUI 窗口。
    /// 关不掉的（窗口正忙）照旧留着显示断连横幅，不因此卡住退出。
    /// </summary>
    private static void CloseUiWindows()
    {
        foreach (var p in Process.GetProcessesByName("ClashAcry.UI"))
        {
            try { p.CloseMainWindow(); }
            catch (Exception) { /* 进程正在消失 / 句柄已失效：退出钩子不为它发明第二种失败表达 */ }
            finally { p.Dispose(); }
        }
    }

    private void PumpTooltip(CancellationToken ct)
    {
        // 只在文案真的变了时才 NIM_MODIFY：图标上正显示的提示气泡会被一次 MODIFY 打断，
        // 无条件每 2 s 刷一次等于「没人能读完 tooltip」。状态没变的常态下这条循环什么都不做。
        string? last = null;
        while (!ct.IsCancellationRequested)
        {
            var tip = $"ClashAcry · {StateText(Core.State)} · {(SystemProxy.IsEnabled() ? "系统代理开" : "系统代理关")} · {_store.Load().MixedPort}";
            if (tip != last) { _icon?.SetTip(tip); last = tip; }
            try { Task.Delay(2000, ct).Wait(ct); } catch { }   // 专用泵线程上的同步等待，见计划 Task 10 Step 5 的说明
        }
    }

    private static string StateText(CoreState s) => s switch
    {
        CoreState.Stopped => "内核未运行", CoreState.Starting => "启动中", CoreState.Running => "内核运行中",
        CoreState.Crashed => "内核异常", CoreState.Updating => "更新中", _ => s.ToString()
    };

    // 纯 Win32 托盘最常见的坑：WM_TRAYICON 回调只在 GetMessageW 循环里被派发。
    // 管道起起来却不进消息循环 = 图标出现、点了没反应。所以 MessageLoop 必须是 RunAsync 的最后一步（同步跑住）。
    private static void MessageLoop(CancellationToken ct)
    {
        while (NativeMethods.GetMessageW(out var m, IntPtr.Zero, 0, 0) && !ct.IsCancellationRequested)
        { NativeMethods.TranslateMessage(ref m); NativeMethods.DispatchMessageW(ref m); }
    }
}
