using System.Threading;

namespace ClashAcry.UI.Tests;

/// <summary>
/// backdrop 三档的 UI 侧判据（Task 13 Step 5）：**只断言"标注说得对"**，材质本身看不见也量不出，
/// 留给 Step 6 的肉眼与 Task 16 的像素取样。
/// 本机实测（写这批用例前的探针）：RadioButton 的 UIA 只支持 <c>SelectionItem</c>，
/// <c>Toggle</c> 是 <c>PatternNotSupportedException</c>（R-13c），所以一律走 <see cref="Select"/>；
/// <c>--reset-prefs</c> 保证"默认档"这条判据不被上一条用例写进去的偏好污染（R-13d）。
/// </summary>
public class BackdropTests : IClassFixture<UiAppHarness>
{
    private readonly UiAppHarness _harness;
    public BackdropTests(UiAppHarness harness) => _harness = harness;

    [Fact]
    public void DefaultRequestsAcrylic_AndLabelIsConsistent()
    {
        using var s = OpenSettings("--reset-prefs", "--tray-absent");
        Assert.Equal("Acrylic", s.Require("BackdropSelectedLabel").Name);

        var label = s.Require("BackdropEffectiveLabel").Name;
        if (label.Contains("亚克力")) Assert.DoesNotContain("（", label);                  // 没降级就不许有话
        else Assert.Contains("（", label);                                              // 降级必须写明原因（spec §8）
    }

    [Fact]
    public void SwitchingToSolid_RestatesLabelsWithoutReopeningWindow()
    {
        using var s = OpenSettings("--reset-prefs", "--tray-absent");
        var hwndBefore = s.Hwnd;

        Select(s, "BackdropSolid");
        Assert.Equal("Solid", s.Require("BackdropSelectedLabel").Name);
        Assert.Equal("当前实际生效：纯色", s.Require("BackdropEffectiveLabel").Name);   // 纯色是请求档，不算降级
        Assert.Equal(hwndBefore, s.Hwnd);                          // 换档不重开窗（spec §8 即时切换）
    }

    [Fact]
    public void Degradation_SpellsOutTheReason()
    {
        // --force-degrade 把「系统允许透明效果」注入成假：真机上这条要靠改用户的「设置 → 个性化 → 颜色 →
        // 透明效果」，测试不许动别人的机器（R-13a）。判据与计划 Step 6 第②项一致，只是可复现。
        using var s = OpenSettings("--reset-prefs", "--tray-absent", "--force-degrade");
        Assert.Equal("Acrylic", s.Require("BackdropSelectedLabel").Name);
        Assert.Equal("当前实际生效：纯色（系统透明效果已关闭）", s.Require("BackdropEffectiveLabel").Name);
    }

    [Fact]
    public void PrefsSurviveRestart()
    {
        using (var s = OpenSettings("--reset-prefs", "--tray-absent"))
        {
            Select(s, "BackdropMica");
            Assert.Equal("Mica", s.Require("BackdropSelectedLabel").Name);
            s.Window.Close();                        // 关窗即退，偏好必须已经落进 LocalSettings
            Assert.True(s.Process.WaitForExit(10000), "关窗后 UI 进程没退");
        }

        using var again = OpenSettings("--tray-absent");                       // 不带 --reset-prefs：读回来的必须是存过的
        Assert.Equal("Mica", again.Require("BackdropSelectedLabel").Name);

        Select(again, "BackdropAcrylic");        // 收尾把偏好送回默认档，别给用户留下一个"Mica 机器"
    }

    private UiSession OpenSettings(params string[] args)
    {
        var s = _harness.Launch(args);
        Select(s, "NavSettings", "BackdropSelectedLabel");
        return s;
    }

    /// <summary>
    /// 点一下并等它落地。等的是**界面真的换了**而不是 pattern 调用返回：WinUI 的选中处理排在消息循环里，
    /// 立刻读标签会拿到旧值（FlaUI 5 的 RadioButton 又没有 Toggle pattern 可查，R-13c）。
    /// </summary>
    private static void Select(UiSession s, string automationId, string? settledLabel = null)
    {
        s.Require(automationId).Patterns.SelectionItem.Pattern.Select();
        if (settledLabel is not null)
        {
            var deadline = DateTime.UtcNow.AddSeconds(8);
            while (DateTime.UtcNow < deadline
                   && s.Window.FindFirstDescendant(cf => cf.ByAutomationId(settledLabel)) is null)
                Thread.Sleep(50);
        }
        Thread.Sleep(300);
    }
}
