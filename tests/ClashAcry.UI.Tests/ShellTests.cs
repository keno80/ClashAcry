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
    public async Task WindowOpensWithThreeNavItems()
    {
        await using var tray = await FakeTray.StartAsync();      // 界面与托盘同生死，起窗口就得有个托盘在
        using var s = _harness.Launch("--reset-prefs");
        Assert.Equal("ClashAcry", s.Window.Title);
        Assert.NotNull(s.Window.FindFirstDescendant(cf => cf.ByAutomationId("NavOverview")));
        Assert.NotNull(s.Window.FindFirstDescendant(cf => cf.ByAutomationId("NavProxies")));
        Assert.NotNull(s.Window.FindFirstDescendant(cf => cf.ByAutomationId("NavSettings")));
    }

    [Fact]
    public void TrayAbsent_BannerSaysWhy_ThenUiQuitsItself()
    {
        // 用户裁定（2026-10-28，第六轮）：**界面不该在没有托盘的房间里独活**。
        // 上一轮为这条状态加的「一键拉起托盘」被明确否掉——不必留一条横幅让用户守着个灰界面，
        // 托盘起不来就一起不玩。--tray-absent 跳过拉起，把这条变成可复现状态。
        // 三段判据按时间排：先看见"为什么"（横幅 + 控制置灰），然后**没有**任何恢复按钮，最后宽限一尽界面自己收。
        using var s = _harness.Launch("--tray-absent", "--reset-prefs");
        Assert.Contains("托盘宿主未运行", Text(s, "BannerText"));
        Assert.False(s.Require("CoreToggle").IsEnabled);         // 控制不了的东西不许做出"能控制"的样子
        Assert.Equal("不可用", Text(s, "CoreStateLabel"));
        Assert.True(NoRelaunchAffordance(s), "横幅不该再挂「一键拉起托盘」——恢复入口已被裁掉");

        Assert.True(s.Process.WaitForExit(25000), "托盘起不来时界面没有自行退出（还留着那条灰横幅）");
    }

    [Fact]
    public async Task TrayDiesMidSession_UiQuitsItself()
    {
        // 与 R-20a 配对的那一半：**同生也同死**。托盘菜单「退出」会关掉界面是上一轮做的；
        // 托盘被强杀/崩掉时没有退出钩子，界面原本留在"横幅念着 Pipe is broken + 控制全灰"的形态。
        // 现在宽限（3 轮轮询）一尽就自己退——不给用户留一个看着像还在、其实什么都做不了的窗口。
        await using var tray = await FakeTray.StartAsync();
        using var s = _harness.Launch("--reset-prefs");
        Assert.True(await tray.WaitUiConnectedAsync(TimeSpan.FromSeconds(10)), "界面没连上假 Tray");
        WaitFor(s, () => Text(s, "CoreVersionLabel") == "v-test", 15);   // 真说上话了，下面的"断"才是断在半路

        await tray.DisposeAsync();                                       // 模拟硬杀：没有退出钩子、连接直接死
        Assert.True(s.Process.WaitForExit(25000), "托盘断了且宽限内没续上，界面没有自行退出");
    }

    [Fact]
    public async Task SwitchPages_ReusesTheSamePageInstance()
    {
        // 「轮询/切页不得有可见抖动」的机制判据：页面实例按 Tag 缓存（MainWindow._pages）。
        // 每个页面实例在构造时把自己的序号写进 UIA Name（PageIdentity.Stamp），来回切页读到同一个序号
        // 就说明是同一个实例；改成每次新建，这里立刻变红。
        await using var tray = await FakeTray.StartAsync();
        using var s = _harness.Launch("--reset-prefs");

        var before = Show(s, "NavProxies").Name;
        Show(s, "NavOverview");
        var after = Show(s, "NavProxies").Name;

        Assert.Equal("ProxiesPage#1", before);          // 首个页面实例的号也顺手钉住「没有偷偷多建」
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task SecondInstance_ExitsImmediatelyAndLeavesFirstAlive()
    {
        // spec §3 的单实例约束：第二份 UI 意味着两个渲染面读同一份 handshake，
        // 最坏是用户在两个窗口里对同一个内核下相反的命令。
        await using var tray = await FakeTray.StartAsync();
        using var first = _harness.Launch("--reset-prefs");
        using var second = _harness.StartBare("--reset-prefs");

        Assert.True(second.WaitForExit(15000), "第二实例没有自行退出");
        Assert.False(first.App.HasExited);                       // 第一份还在
        Assert.Equal("ClashAcry", first.Window.Title);           // 而且窗口没被第二次的 Exit 连带拆掉
    }

    [Fact]
    public async Task ClosingWindow_ExitsTheProcess()
    {
        // spec §3 的「关窗即退」：UI 不常驻、不藏进托盘（藏起来就意味着第二个渲染面随时可能复活）。
        await using var tray = await FakeTray.StartAsync();
        using var s = _harness.Launch("--reset-prefs");
        s.Window.Close();
        Assert.True(s.Process.WaitForExit(10000), "关窗后 UI 进程没退");
        Assert.Equal(0, s.Process.ExitCode);
    }

    [Fact]
    public async Task CoreCrashed_BannerShowsFault_ButUiStaysAlive_AndNoRelaunchAffordance()
    {
        // 「一起死」指的是**跟托盘**死，不是跟内核死：内核崩了托盘还在，用户要的是在同一个界面里
        // 把内核再按起来（概览页那个开关）。把这条与上一条对照着看，就知道退出判据取的是
        // "够不够得着托盘"而不是"内核好不好"。同时这里钉住恢复按钮已经拆掉（第六轮裁定）。
        await using var tray = await FakeTray.StartAsync();
        using var s = _harness.Launch("--reset-prefs");
        Assert.True(await tray.WaitUiConnectedAsync(TimeSpan.FromSeconds(10)), "界面没连上假 Tray");
        WaitFor(s, () => Text(s, "CoreStateLabel") == "未运行", 15);     // 握手已落地，界面真的在跟假 Tray 说话

        tray.Emit(EventTypes.CoreStateChanged,
            new CoreStateChanged(CoreState.Crashed, "配置解析失败", Array.Empty<string>()));
        WaitFor(s, () => !s.Require("Banner").IsOffscreen && Text(s, "BannerText").Contains("配置解析失败"), 10);
        Assert.True(NoRelaunchAffordance(s), "横幅不该再挂「一键拉起托盘」——恢复入口已被裁掉");

        // 跨过宽限期（断连那条判据是 3 轮 ≈6 s）还活着才算"内核崩了不误退"。
        Thread.Sleep(9000);
        Assert.False(s.Process.HasExited, "内核崩了、托盘还在，界面不该把自己关掉");
    }

    /// <summary>恢复入口整条撤掉：WinUI 里 <c>Visibility.Collapsed</c> 的元素不进 UIA 树，所以"没有"就是"找不到"。</summary>
    private static bool NoRelaunchAffordance(UiSession s) =>
        s.Window.FindFirstDescendant(cf => cf.ByAutomationId("RelaunchTrayButton")) is null;

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
