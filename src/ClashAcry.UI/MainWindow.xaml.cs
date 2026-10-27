using ClashAcry.Core;
using ClashAcry.UI.Backdrops;
using ClashAcry.UI.Services;
using ClashAcry.UI.ViewModels;
using ClashAcry.UI.Views;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClashAcry.UI;

/// <summary>
/// 主窗口：状态横幅 + 左侧导航三页。<see cref="Link"/> 是 UI 与 Tray 的唯一通道
/// （spec §3 禁令 1：内核生命周期全在 Tray，UI 只会发命令、读回包）。
/// 界面偏好（材质、主题）走 <see cref="UiPrefs"/> 的 LocalSettings，绝不落 <c>settings.json</c>（两棵真相树）。
/// </summary>
public sealed partial class MainWindow : Window
{
    public TrayLink Link { get; } = new();
    public UiPrefs Prefs { get; } = new();
    public BackdropService Backdrop { get; } = new(
        systemAllows: App.ForceDegradeRequested ? () => false : null);

    /// <summary>
    /// 外壳级状态机：概览页与设置页**共用这一个实例**（R-14d）。页面各自轮询会把同一条管道压满，
    /// 还会让「系统代理 / 测速地址」在两页显示成两个值；轮询周期与故障文案也因此只有一处能说话。
    /// </summary>
    public OverviewViewModel Overview { get; }

    private string _lastFault = "";

    /// <summary>窗口句柄：系统选择器（FileOpenPicker）要挂在它上面，否则 unpackaged 应用点了没反应。</summary>
    public nint Handle => WinRT.Interop.WindowNative.GetWindowHandle(this);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    /// <summary>
    /// 页面实例按 Tag 复用。每次切页都新建意味着重新握手 + 重拉 <c>/proxies</c>，
    /// 整页白一下——正是「轮询/切换不得有可见抖动」那条红线的第一现场。
    /// </summary>
    private readonly Dictionary<string, Page> _pages = new();

    public MainWindow()
    {
        InitializeComponent();
        Title = "ClashAcry";                       // FlaUI 的 ShellTests 断言这个字符串
        // AppWindow.Resize 吃的是**物理像素**：125% 缩放上直接写 1000x680 会缩成 800x544 DIP，
        // 界面比设计稿小一圈（自测时靠这个把 UIA 矩形和位图对齐了，见 app.manifest 的 PerMonitorV2）。
        var dpi = (int)GetDpiForWindow(Handle);
        if (dpi == 0) dpi = 96;                    // 窗口还没落位时返回 0，按 1x 兜底，别把尺寸算成 0
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1000 * dpi / 96, 680 * dpi / 96));

        Overview = new OverviewViewModel(Link, DispatcherQueue.GetForCurrentThread());
        Overview.Changed += RenderBanner;

        Nav.SelectionChanged += (_, e) => Show(TagOf(e.SelectedItem));
        Root.Loaded += async (_, _) => { await ConnectAsync(); Overview.Start(); };
        Closed += (_, _) => Overview.Dispose();

        if (App.ResetPrefsRequested) Prefs.Reset();
        if (App.ForceDarkRequested)
        {
            // 只钉内容面，不写偏好（写了就是拿一条测试参数改用户的界面设置，下一条默认档用例会被它污染）。
            // 材质自己会跟根元素明暗走（R-13b），这里仍按当前偏好重挂一次，保证色调与界面一致。
            Root.RequestedTheme = ElementTheme.Dark;
            ApplyBackdrop(BackdropFromPref(Prefs.Backdrop));
        }
        else
        {
            ApplyTheme(ThemeFromPref(Prefs.Theme));      // ApplyTheme 内部会重挂材质
        }

        Nav.SelectedItem = Nav.MenuItems[0];       // 默认页 = 概览（SelectionChanged 会把它 Show 出来）
        Show("overview");                          // 事件在窗口可见后才起效的话，这里兜一次底（同一实例，无副作用）
    }

    private static string? TagOf(object? selected) => (selected as NavigationViewItem)?.Tag as string;

    private void Show(string? tag)
    {
        if (tag is null) return;
        if (!_pages.TryGetValue(tag, out var page))
        {
            page = tag switch
            {
                "proxies" => new ProxiesPage(this),
                "settings" => new SettingsPage(this),
                _ => new OverviewPage(this),
            };
            _pages[tag] = page;
        }
        RootFrame.Content = page;
    }

    /// <summary>切材质：即时生效，不重启、不重开窗（spec §8）。窗口句柄不变这条由 BackdropTests 钉住。</summary>
    public void ApplyBackdrop(BackdropKind kind)
    {
        Backdrop.Apply(this, kind);
        Prefs.Backdrop = kind.ToString().ToLowerInvariant();
    }

    public void ApplyTheme(ElementTheme theme)
    {
        Root.RequestedTheme = theme;               // 内容面（含导航、卡片）跟着换
        Prefs.Theme = ThemeToPref(theme);
        ApplyBackdrop(BackdropFromPref(Prefs.Backdrop));   // backdrop 的明暗跟内容面走，否则亚克力色调和界面错位
    }

    /// <summary><c>Default</c> 就是「跟随系统」；设置页只会传这三种之一。</summary>
    public ElementTheme CurrentTheme => Root.RequestedTheme;

    private static BackdropKind BackdropFromPref(string? pref) =>
        Enum.TryParse<BackdropKind>(pref, ignoreCase: true, out var k) ? k : BackdropKind.Acrylic;

    private static ElementTheme ThemeFromPref(string? pref) => pref switch
    {
        "light" => ElementTheme.Light,
        "dark" => ElementTheme.Dark,
        _ => ElementTheme.Default,
    };

    private static string ThemeToPref(ElementTheme theme) => theme switch
    {
        ElementTheme.Light => "light",
        ElementTheme.Dark => "dark",
        _ => "system",
    };

    private async Task ConnectAsync()
    {
        if (App.TrayAbsentRequested)
        {
            // 故障从 Overview 走（它是横幅唯一的文本来源，R-14e）：直接 ShowBanner 的话，
            // 轮询第一次撞上「未连接」就会把这句话盖成另一条措辞。
            Overview.ReportFault(TrayAbsentText);
            return;
        }
        if (!await Link.EnsureTrayRunningAsync()) { Overview.ReportFault(TrayAbsentText); return; }
        if (await Link.HandshakeAsync() is null)
            Overview.ReportFault("托盘无应答，界面显示的可能不是内核当前状态");
    }

    private const string TrayAbsentText = "托盘宿主未运行，内核控制不可用";

    /// <summary>横幅只跟随连接级故障的**边界**：故障出现时写下，恢复时收起；坏着期间不重述（同一条措辞由 VM 钉住）。</summary>
    private void RenderBanner()
    {
        var fault = Overview.ErrorText;
        if (fault == _lastFault) return;
        _lastFault = fault;
        if (fault.Length == 0) ClearBanner();
        else ShowBanner(fault);
    }

    /// <summary>行内反馈：不弹阻塞对话框（用户红线）。</summary>
    public void ShowBanner(string text)
    {
        BannerText.Text = text;
        Banner.IsOpen = true;
    }

    public void ClearBanner() => Banner.IsOpen = false;
}
