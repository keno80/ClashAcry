using ClashAcry.UI.Services;
using ClashAcry.UI.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClashAcry.UI;

/// <summary>
/// 主窗口：状态横幅 + 左侧导航三页。<see cref="Link"/> 是 UI 与 Tray 的唯一通道
/// （spec §3 禁令 1：内核生命周期全在 Tray，UI 只会发命令、读回包）。
/// </summary>
public sealed partial class MainWindow : Window
{
    public TrayLink Link { get; } = new();

    /// <summary>
    /// 页面实例按 Tag 复用。每次切页都新建意味着重新握手 + 重拉 <c>/proxies</c>，
    /// 整页白一下——正是「轮询/切换不得有可见抖动」那条红线的第一现场。
    /// </summary>
    private readonly Dictionary<string, Page> _pages = new();

    public MainWindow()
    {
        InitializeComponent();
        Title = "ClashAcry";                       // FlaUI 的 ShellTests 断言这个字符串
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1000, 680));

        Nav.SelectionChanged += (_, e) => Show(TagOf(e.SelectedItem));
        Root.Loaded += async (_, _) => await ConnectAsync();

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
                "proxies" => new ProxiesPage(),
                "settings" => new SettingsPage(),
                _ => new OverviewPage(),
            };
            _pages[tag] = page;
        }
        RootFrame.Content = page;
    }

    private async Task ConnectAsync()
    {
        if (App.TrayAbsentRequested)
        {
            ShowBanner(TrayAbsentText);            // 测试参数：把「Tray 不在」变成可复现状态，而不是靠机器上真没跑
            return;
        }
        if (!await Link.EnsureTrayRunningAsync()) { ShowBanner(TrayAbsentText); return; }
        if (await Link.HandshakeAsync() is null)
            ShowBanner("托盘无应答，界面显示的可能不是内核当前状态");
    }

    private const string TrayAbsentText = "托盘宿主未运行，内核控制不可用";

    /// <summary>行内反馈：不弹阻塞对话框（用户红线）。</summary>
    public void ShowBanner(string text)
    {
        BannerText.Text = text;
        Banner.IsOpen = true;
    }
}
