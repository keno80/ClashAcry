using System.Threading;
using ClashAcry.Contracts;
using ClashAcry.UI.Tests.Fakes;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Exceptions;
using FlaUI.Core.Input;

namespace ClashAcry.UI.Tests;

/// <summary>
/// 概览页与设置页导入区的回归锁（Task 14）。手法：<b>测试进程里起一台真 PipeServer 当假 Tray</b>
/// （见 <see cref="FakeTray"/>），界面的探活直接命中它——否则「崩溃」「进度推进」这些事件驱动的状态
/// 只能靠人点击，根本断不了。
/// 四条共同约定（都是前两个任务实测换来的）：
/// ① 状态一律读 UI 自己写出来的文本（<c>*Label</c>），不读控件 pattern（R-12i/R-13c 同一条理由）；
/// ② 驱动控件走 pattern（Toggle/Invoke/Value/SetFocus），不用鼠标 <c>Click()</c>——那会把用户的光标抢走；
/// ③ 断言"界面变了"必须等，不能点完就读：WinUI 的处理排在消息循环里（R-13c）；
/// ④ UI.Tests 只引用 Core，不引用 UI 工程（R-12f）。
/// </summary>
public class OverviewTests : IClassFixture<UiAppHarness>
{
    private readonly UiAppHarness _harness;
    public OverviewTests(UiAppHarness harness) => _harness = harness;

    [Fact]
    public async Task CrashedEvent_ShowsAbnormalState_AndRawStderrInLog()
    {
        await using var tray = await FakeTray.StartAsync();
        using var s = _harness.Launch("--reset-prefs");
        Assert.True(await tray.WaitUiConnectedAsync(TimeSpan.FromSeconds(10)), "界面没连上假 Tray");
        await ReadyAsync(s);

        tray.Emit(EventTypes.CoreStateChanged, new CoreStateChanged(CoreState.Crashed,
            "connection refused", new[] { "time=2026-10-27T00:00:00 level=error msg=Start Mixed(http+socks) server error: listen tcp 127.0.0.1:7897: bind: address already in use" }));

        await WaitUntilAsync(() => Text(s, "CoreStateLabel").Contains("异常"), 5);
        Press(s, "LogButton");                                   // 展开内核输出
        await WaitUntilAsync(() => Text(s, "LogText").Contains("address already in use"), 5);
        Assert.DoesNotContain("地址已被使用", Text(s, "LogText"));     // Go 原文不翻译（spec §9）
    }

    [Fact]
    public async Task UpdateClick_DisablesButton_ShowsPercent_AndRestoresOnResult()
    {
        await using var tray = await FakeTray.StartAsync();
        using var s = _harness.Launch("--reset-prefs");
        Assert.True(await tray.WaitUiConnectedAsync(TimeSpan.FromSeconds(10)));
        await ReadyAsync(s);
        Assert.Equal("v-test", Text(s, "CoreVersionLabel"));     // 版本行来自握手，不是进度

        Press(s, "UpdateButton");
        await WaitUntilAsync(() => Text(s, "UpdateButton").Contains("%"), 5);   // 点下去立刻有反馈，不许"点了没反应"
        Assert.False(s.Require("UpdateButton").IsEnabled);                       // 进行中禁止重复提交
        Assert.True(tray.Saw(CmdTypes.CoreUpdate));                              // 下载在 Tray，UI 只发命令（spec §3 禁令 1）

        tray.Emit(EventTypes.UpdateProgress, new UpdateProgress(37 * 1024, 102_400, 37));
        await WaitUntilAsync(() => Text(s, "UpdateButton").Contains("37%"), 5);
        tray.Emit(EventTypes.UpdateProgress, new UpdateProgress(102_400, 102_400, 100));
        await WaitUntilAsync(() => Text(s, "UpdateButton").Contains("100%"), 5);

        // 下载满 100% 还不算完：解包/换文件仍在地走，文案必须留在"进行中"，直到结果帧（spec §9）。
        Assert.Contains("%", Text(s, "UpdateButton"));

        // 换核成功后 Tray 才把新版本写进 settings；界面靠重新握手取，进度数字绝不冒充版本号。
        tray.Handshake = tray.Handshake with { CoreVersion = "v-new" };
        tray.CompleteUpdate(UpdateResult.Updated);
        await WaitUntilAsync(() => Text(s, "UpdateButton") == "更新内核" && s.Require("UpdateButton").IsEnabled, 5);
        await WaitUntilAsync(() => Text(s, "CoreVersionLabel") == "v-new", 5);
    }

    [Fact]
    public async Task SilentRefresh_LeavesEveryVisibleTextNodeUntouched()
    {
        await using var tray = await FakeTray.StartAsync();
        using var s = _harness.Launch("--reset-prefs");
        Assert.True(await tray.WaitUiConnectedAsync(TimeSpan.FromSeconds(10)));
        await ReadyAsync(s);
        Show(s, "NavProxies");
        Show(s, "NavOverview");

        var before = new[] { "CoreStateLabel", "CoreVersionLabel", "SysProxyStateLabel" }
            .ToDictionary(id => id, id => Text(s, id));
        await Task.Delay(2_600);                                        // 跨过至少一个 2 s 刷新周期

        // 先证明"真的刷过"，否则下面的"文字没变"可能只是因为什么都没发生。
        Assert.True(tray.CommandLog.Count(f => f.Type == CmdTypes.SettingsGet) >= 2,
            $"两秒内没看到第二次轮询：{string.Join(",", tray.CommandLog.Select(f => f.Type))}");
        foreach (var (id, text) in before)
            Assert.Equal(text, Text(s, id));                            // 用户红线：刷新不得留下可见痕迹
    }

    [Fact]
    public async Task CoreToggle_On_SendsCoreStart_AndWaitsForStateEvent()
    {
        await using var tray = await FakeTray.StartAsync();
        using var s = _harness.Launch("--reset-prefs");
        Assert.True(await tray.WaitUiConnectedAsync(TimeSpan.FromSeconds(10)));
        await ReadyAsync(s);
        Assert.Equal("未运行", Text(s, "CoreStateLabel"));

        Press(s, "CoreToggle");
        await WaitUntilAsync(() => tray.Saw(CmdTypes.CoreStart), 5);
        Thread.Sleep(300);
        Assert.Equal("未运行", Text(s, "CoreStateLabel"));        // spec §3 禁令 3 的界面侧：点下去 ≠ 已启动，不许乐观翻转

        tray.Emit(EventTypes.CoreStateChanged, new CoreStateChanged(CoreState.Running, null, Array.Empty<string>()));
        await WaitUntilAsync(() => Text(s, "CoreStateLabel") == "运行中", 5);
    }

    [Fact]
    public async Task ImportUrl_FallsThroughToTray_AndShowsResultInline()
    {
        await using var tray = await FakeTray.StartAsync();
        using var s = _harness.Launch("--reset-prefs");
        Assert.True(await tray.WaitUiConnectedAsync(TimeSpan.FromSeconds(10)));
        await ReadyAsync(s);
        Show(s, "NavSettings");

        Assert.Equal("", Text(s, "ImportResult"));                     // 初始必须空着，否则下面那条断言会偶然成立
        TypeText(s, "ImportUrlBox", "http://127.0.0.1:1/sub.yaml");
        Press(s, "ImportButton");

        Assert.True(tray.Saw(CmdTypes.SubImport));                     // 导入只在 Tray 执行（写 config.yaml 的唯一写者）
        var sent = tray.CommandLog.Last(f => f.Type == CmdTypes.SubImport);
        Assert.Equal("http://127.0.0.1:1/sub.yaml", JsonUtil.Read<ImportRequest>(sent.Payload)!.Url);

        await WaitUntilAsync(() => Text(s, "ImportResult").Contains("已导入"), 10);
        Assert.Equal("导入订阅", Text(s, "ImportButton"));              // 进行中文案必须收回
    }

    [Fact]
    public void TrayAbsent_CoreToggleDisabled_AndBannerShown()
    {
        // Task 12 欠下的那半条（R-12h）：那时还没有内核开关，只能断言横幅。现在补齐判据。
        using var s = _harness.Launch("--tray-absent", "--reset-prefs");
        Assert.Contains("托盘宿主未运行", Text(s, "BannerText"));
        Assert.False(s.Require("CoreToggle").IsEnabled);               // 控制不了的东西不许做出"能控制"的样子
        Assert.Equal("不可用", Text(s, "CoreStateLabel"));
    }

    /// <summary>
    /// 读一条文本判据；元素缺失直接失败，不返回 null 让后面的断言拿空串蒙混。
    /// 实测（本机 WinUI 3 + FlaUI 5）：**文本为空的 TextBlock 的 UIA Name 是 NOTSUPPORTED，不是空串**
    /// （ControlType 仍是 Text，元素也在树里）。空 = 「还没写结果」，所以这一条读不到就当空，
    /// 一旦有文本 Name 立刻可读——「初始必须空着」这条断言的含义没有因此变松。
    /// </summary>
    private static string Text(UiSession s, string id)
    {
        try { return s.Require(id).Name; }
        catch (PropertyNotSupportedException) { return ""; }
    }

    /// <summary>
    /// 等界面真的和假 Tray 说上话（握手回包里版本号落地、状态打底完成）。
    /// 不等就断言会拿到「未知 / 不可用」这套初始值——那是还没数据的界面，不是本次要测的状态。
    /// </summary>
    private static Task ReadyAsync(UiSession s) =>
        WaitUntilAsync(() => Text(s, "CoreVersionLabel") == "v-test" && Text(s, "CoreStateLabel") == "未运行", 15);

    /// <summary>
    /// 按控件类型选驱动方式：开关用 Toggle、按钮用 Invoke，都不支持才退回鼠标点击。
    /// 实测 WinUI 3 的 RadioButton 只认 SelectionItem（R-13c），所以它单独走 <see cref="Show"/> 那条路。
    /// </summary>
    private static void Press(UiSession s, string id)
    {
        var e = s.Require(id);
        if (e.Patterns.Toggle.IsSupported) e.Patterns.Toggle.Pattern.Toggle();
        else if (e.Patterns.Invoke.IsSupported) e.Patterns.Invoke.Pattern.Invoke();
        else e.Click();
    }

    /// <summary>
    /// 写进输入框：走 ValuePattern，不用鼠标点击 + 真键盘——那会把用户的光标抢走（R-12i 同一条理由）。
    /// 不支持就当场说清楚，不悄悄退回"抢光标"那条路。
    /// </summary>
    private static void TypeText(UiSession s, string id, string text)
    {
        var e = s.Require(id);
        Assert.True(e.Patterns.Value.IsSupported, $"{id} 不支持 ValuePattern，键入需要另一条通路");
        e.Patterns.Value.Pattern.SetValue(text);
    }

    private static void Show(UiSession s, string navId)
    {
        s.Require(navId).Patterns.SelectionItem.Pattern.Select();
        var pageId = navId switch { "NavProxies" => "ProxiesPage", "NavSettings" => "SettingsPage", _ => "OverviewPage" };
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < deadline)
        {
            var page = s.Window.FindFirstDescendant(cf => cf.ByAutomationId(pageId));
            if (page is not null && !page.IsOffscreen) return;
            Thread.Sleep(100);
        }
        throw new Xunit.Sdk.XunitException($"切换后未见页面 {pageId}");
    }

    private static async Task WaitUntilAsync(Func<bool> cond, int seconds)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            try { if (cond()) return; } catch (Exception) { /* 界面正在重建这一帧，下一轮再读 */ }
            await Task.Delay(100);
        }
        throw new TimeoutException($"断言等待超时：{seconds} s 内条件未成立");
    }
}
