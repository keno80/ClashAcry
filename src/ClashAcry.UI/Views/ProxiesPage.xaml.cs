using System.Threading;
using ClashAcry.Core;
using ClashAcry.Core.Proxies;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClashAcry.UI.Views;

/// <summary>
/// 节点页：策略组树 + 切换 + 测速 + 订阅（Provider）手动刷新（Task 15）。
/// 三条页内规矩：
/// ① **只读内核，不写引擎设置**：这里发的是 controller REST（<c>PUT /proxies/{组}</c> 一类），
///    与 <c>settings.json</c> 无关（两棵真相树）；
/// ② 合并逻辑全在 <see cref="ProxiesViewModel"/>，页面只提供委托与轮询节拍——抖动判据在 Core 里用
///    <c>Assert.Same</c> 钉住，页面不该再有第二份"要不要重建容器"的判断；
/// ③ 轮询与测速互斥：一轮 <c>/proxies</c> 落在测速中间，会把刚测出的值刷回旧样本，
///    用户看到的就是数字自己跳回去。
/// </summary>
public sealed partial class ProxiesPage : Page
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly MainWindow _shell;
    private readonly ProxiesViewModel _vm;
    private readonly CancellationTokenSource _ct = new();
    private DispatcherTimer? _timer;
    private bool _refreshing;
    private bool _testing;
    private string _hint = "";

    public ProxiesPage(MainWindow shell)
    {
        InitializeComponent();
        PageIdentity.Stamp(this);
        _shell = shell;
        _vm = new ProxiesViewModel(
            ct => Rest().GetProxiesAsync(ct),
            (g, n, ct) => Rest().SetProxyAsync(g, n, ct),
            (n, ct) => Rest().GetDelayAsync(n, _shell.Overview.DelayUrl, 5000, ct),
            (p, ct) => Rest().RefreshProviderAsync(p, ct),
            _shell.Overview.DelayUrl);
        DataContext = _vm;                 // XAML 只绑 Groups / ProviderRows

        Loaded += OnLoaded;
        Unloaded += (_, _) => _timer?.Stop();
    }

    /// <summary>
    /// 客户端来自外壳最近一次握手（secret 只活在那一处，spec §4.1 红线）。
    /// 轮询与测速的续跑都靠 WinUI 的 SynchronizationContext 回到 UI 线程——VM 直接改绑定对象的属性，
    /// 从线程池改会让绑定层抛跨线程异常。
    /// </summary>
    private MihomoClient Rest() => _shell.Overview.Rest
        ?? throw new InvalidOperationException("内核控制器地址还没拿到");

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_timer is null)
        {
            _timer = new DispatcherTimer { Interval = PollInterval };
            _timer.Tick += async (_, _) => await RefreshOnceAsync();
        }
        _timer.Start();
        _ = RefreshOnceAsync();                       // 不等第一个周期
    }

    /// <summary>一轮静默刷新：读 <c>/proxies</c>（组树）+ <c>/providers/proxies</c>（订阅表），合并进 VM。</summary>
    private async Task RefreshOnceAsync()
    {
        if (_refreshing || _testing) return;
        if (_shell.Overview.Rest is null)
        {
            SetHint(_shell.Overview.TrayConnected ? "内核未运行，暂无节点" : "未连接托盘宿主，读不到节点");
            return;
        }
        _refreshing = true;
        try
        {
            var proxies = await Rest().GetProxiesAsync(_ct.Token);
            ProvidersResponse? providers = null;
            try { providers = await Rest().GetProvidersAsync(_ct.Token); }
            catch (Exception) { /* 订阅表读不到不影响组树：Provider 行留在上一次的值，不整表清空 */ }
            await _vm.RefreshAsync(proxies, providers);
            SetHint("");
        }
        catch (OperationCanceledException) { /* 页面已卸载 */ }
        catch (Exception ex) { SetHint($"读取节点失败：{ex.Message}"); }
        finally { _refreshing = false; }
    }

    private async void OnNodeClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ProxyNodeVm node }) return;
        var group = _vm.Groups.FirstOrDefault(g => g.Nodes.Contains(node));
        if (group is null) return;
        try { await _vm.SelectAsync(group, node, _ct.Token); }
        catch (Exception ex) { SetHint($"切换失败：{ex.Message}"); }
    }

    /// <summary>折叠只由用户点尖角触发，轮询永不改它——否则每 2 s 就把用户展开的东西收回去。</summary>
    private void OnCollapseClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ProxyGroupVm group }) group.Expanded = !group.Expanded;
    }

    private async void OnTestGroupClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ProxyGroupVm group }) return;
        await RunTestAsync(() => _vm.TestGroupAsync(group, _ct.Token));
    }

    private async void OnTestAllClick(object sender, RoutedEventArgs e) =>
        await RunTestAsync(() => _vm.TestAllAsync(_ct.Token));

    /// <summary>测速期间锁住按钮（不许重复提交）并暂停轮询；行内的"测速中…"由 VM 的 IsTesting 负责。</summary>
    private async Task RunTestAsync(Func<Task> test)
    {
        if (_testing) return;
        _testing = true;
        TestAllButton.IsEnabled = false;
        try { await test(); SetHint(""); }
        catch (Exception ex) { SetHint($"测速失败：{ex.Message}"); }
        finally { TestAllButton.IsEnabled = true; _testing = false; }
    }

    private async void OnRefreshProviderClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ProviderRowVm row }) return;
        try
        {
            await _vm.RefreshProviderAsync(row.Name, _ct.Token);
            await RefreshOnceAsync();                 // 新 updatedAt 只从服务端读回来，本地不猜
        }
        catch (Exception ex) { SetHint($"刷新订阅失败：{ex.Message}"); }
    }

    private void SetHint(string text)
    {
        if (_hint == text) return;
        _hint = text;
        ProxiesHintLabel.Text = text;
    }
}
