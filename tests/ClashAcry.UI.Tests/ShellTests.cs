using ClashAcry.Contracts;
using ClashAcry.UI.Tests.Fakes;
using FlaUI.Core.AutomationElements;
using System.Threading;

namespace ClashAcry.UI.Tests;

/// <summary>
/// 外壳的回归锁（Task 12 Step 4）。这些用例起**真窗口**，所以只在 UI.Tests 里跑；
/// 测试项目**不引用** UI 程序集（R-12f）：引用它会把 self-contained 的 WinUI 依赖拖进测试输出目录，
/// 还会让"被测的是那个 exe"变成"被测的是测试进程里的一份副本"。
/// </summary>
public class ShellTests : IClassFixture<UiAppHarness>
{
    private readonly UiAppHarness _harness;
    public ShellTests(UiAppHarness harness) => _harness = harness;

    [Fact]
    public void WindowOpensWithThreeNavItems()
    {
        using var s = _harness.Launch();
        Assert.Equal("ClashAcry", s.Window.Title);
        Assert.NotNull(s.Window.FindFirstDescendant(cf => cf.ByAutomationId("NavOverview")));
        Assert.NotNull(s.Window.FindFirstDescendant(cf => cf.ByAutomationId("NavProxies")));
        Assert.NotNull(s.Window.FindFirstDescendant(cf => cf.ByAutomationId("NavSettings")));
    }

    [Fact]
    public void TrayAbsent_ShowsBanner()
    {
        // --tray-absent 把「托盘不在」变成可复现状态：不跑 EnsureTrayRunningAsync，
        // 也就不依赖机器上此刻到底有没有 Tray 在跑（有的话这条会两难）。
        // 计划里这条叫 …DisablesCoreToggle，但 Task 12 的外壳还没有内核开关（在 Task 14），
        // 所以这里只断言横幅可见 + 文案（R-12h）。
        using var s = _harness.Launch("--tray-absent");
        var banner = s.Require("Banner");
        Assert.False(banner.IsOffscreen);                                        // 横幅可见
        Assert.Contains("托盘宿主未运行", s.Require("BannerText").Name);
    }

    [Fact]
    public void SwitchPages_ReusesTheSamePageInstance()
    {
        // 「轮询/切页不得有可见抖动」的机制判据：页面实例按 Tag 缓存（MainWindow._pages）。
        // 每个页面实例在构造时把自己的序号写进 UIA Name（PageIdentity.Stamp），来回切页读到同一个序号
        // 就说明是同一个实例；改成每次新建，这里立刻变红。
        using var s = _harness.Launch("--tray-absent");

        var before = Show(s, "NavProxies").Name;
        Show(s, "NavOverview");
        var after = Show(s, "NavProxies").Name;

        Assert.Equal("ProxiesPage#1", before);          // 首个页面实例的号也顺手钉住「没有偷偷多建」
        Assert.Equal(before, after);
    }

    [Fact]
    public void SecondInstance_ExitsImmediatelyAndLeavesFirstAlive()
    {
        // spec §3 的单实例约束：第二份 UI 意味着两个渲染面读同一份 handshake，
        // 最坏是用户在两个窗口里对同一个内核下相反的命令。
        using var first = _harness.Launch("--tray-absent");
        using var second = _harness.StartBare("--tray-absent");

        Assert.True(second.WaitForExit(15000), "第二实例没有自行退出");
        Assert.False(first.App.HasExited);                       // 第一份还在
        Assert.Equal("ClashAcry", first.Window.Title);           // 而且窗口没被第二次的 Exit 连带拆掉
    }

    [Fact]
    public void ClosingWindow_ExitsTheProcess()
    {
        // spec §3 的「关窗即退」：UI 不常驻、不藏进托盘（藏起来就意味着第二个渲染面随时可能复活）。
        using var s = _harness.Launch("--tray-absent");
        s.Window.Close();
        Assert.True(s.Process.WaitForExit(10000), "关窗后 UI 进程没退");
        Assert.Equal(0, s.Process.ExitCode);
    }

    [Fact]
    public void TrayAbsent_BannerOffersOneClickRelaunch()
    {
        // v1.1 欠账②「一键拉起」：托盘退出后横幅只把故障念给用户听，恢复入口要用户自己去找 exe。
        // UI 本来就有这条通路（外壳启动时的 EnsureTrayRunningAsync），欠的是把它交给用户按一下。
        using var s = _harness.Launch("--tray-absent");
        var button = s.Require("RelaunchTrayButton");
        Assert.False(button.IsOffscreen);                       // 连接断了 = 这条动作此刻正是用户要的，不许藏着
        Assert.Contains("拉起", button.Name);
    }

    [Fact]
    public async Task CoreCrashed_BannerShowsFault_ButNoRelaunchAction()
    {
        // 按钮的可见判据必须是「**托盘**够不着」，不是「横幅开着」。
        // 内核崩了时托盘活得好好的、横幅也开着，这时候给一个「拉起托盘」是把用户往错的方向推——
        // 那条正确的动作是概览页上的内核开关。naive 地按横幅可见来显示按钮会在这里变红。
        await using var tray = await FakeTray.StartAsync();
        using var s = _harness.Launch("--reset-prefs");
        Assert.True(await tray.WaitUiConnectedAsync(TimeSpan.FromSeconds(10)), "界面没连上假 Tray");
        WaitFor(s, () => Text(s, "CoreStateLabel") == "未运行", 15);     // 握手已落地，界面真的在跟假 Tray 说话

        tray.Emit(EventTypes.CoreStateChanged,
            new CoreStateChanged(CoreState.Crashed, "配置解析失败", Array.Empty<string>()));
        WaitFor(s, () => !s.Require("Banner").IsOffscreen && Text(s, "BannerText").Contains("配置解析失败"), 10);

        var button = s.Window.FindFirstDescendant(cf => cf.ByAutomationId("RelaunchTrayButton"));
        Assert.True(button is null || button.IsOffscreen, "托盘在线、只是内核崩了，不该给「拉起托盘」");
    }

    private static string Text(UiSession s, string id)
    {
        try { return s.Require(id).Name; }
        catch (FlaUI.Core.Exceptions.PropertyNotSupportedException) { return ""; }   // 空文本的 TextBlock：Name 不可读
    }

    private static void WaitFor(UiSession s, Func<bool> cond, int seconds)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            try { if (cond()) return; } catch (Exception) { /* 这一帧正在重建，下一轮再读 */ }
            Thread.Sleep(100);
        }
        throw new Xunit.Sdk.XunitException($"界面等待超时（{seconds} s）");
    }

    /// <summary>点导航项并等对应页面出现在树里；返回页面元素。</summary>
    private static AutomationElement Show(UiSession s, string navId)
    {
        var item = s.Require(navId);
        // R-12i（实测）：WinUI 3 的 NavigationViewItem **不实现 Invoke**（Patterns.Invoke 抛
        // PatternNotSupportedException），它实现的是 SelectionItem。用 Select 而不是鼠标 Click：
        // Click 会把用户的光标抢走。
        item.Patterns.SelectionItem.Pattern.Select();

        var pageId = navId switch { "NavProxies" => "ProxiesPage", "NavSettings" => "SettingsPage", _ => "OverviewPage" };
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < deadline)
        {
            var page = s.Window.FindFirstDescendant(cf => cf.ByAutomationId(pageId));
            if (page is not null && !page.IsOffscreen) return page;
            Thread.Sleep(100);
        }
        throw new Xunit.Sdk.XunitException($"切换后未见页面 {pageId}");
    }
}
