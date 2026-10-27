using System.Drawing;
using ClashAcry.Contracts;
using ClashAcry.UI.Tests.Fakes;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace ClashAcry.UI.Tests;

/// <summary>
/// 深色 chrome 的**逐类像素核对**（Task 16）。AGENTS.md 的硬约束写明判据是像素而不是肉眼看截图，
/// 所以每条断言都落在「某个控件的某个相对坐标上取到的 RGB」上；控件类别按约束清单逐个覆盖：
/// <c>Button</c> / <c>ComboBox</c> / <c>ComboBoxItem</c> / 自绘节点行 / <c>ScrollBar</c>（轨道、滑块、翻页 RepeatButton）。
/// 每条都是**双极性**的：同一块上既要点出「底是暗的」，又要点出「前景（文字/箭头/滑块）是亮的」——
/// 只量暗会把「整屏糊成黑」也判成通过，那正是深色档最常见的假通过形态。
/// 阈值来自取样探针的实测（R-16 段）：深色 chrome 落在 39~88，浅色前景落在 160~255，
/// 中间隔着一条 70 级的空档，所以 <see cref="DarkMax"/>=110 与 <see cref="ForegroundMin"/>=140 两侧都有余量。
/// 三条夹具约定：
/// ① 一律 <c>--force-dark</c>：不写进 <see cref="UiPrefs"/>（那是用户的偏好，测试不许改），只在本次进程里钉深色；
/// ② 取样前先确认元素有非零矩形——WinUI 对**视口外**的元素报 0×0，拿它取样等于这条核对没跑；
/// ③ 弹层（ComboBox 下拉）不在主窗的合成面里，PrintWindow 抓不到，必须走整屏抓取（见 <see cref="ScreenCapture"/>）。
/// </summary>
public sealed class ChromeContrastTests : IClassFixture<UiAppHarness>, IDisposable
{
    /// <summary>深色 chrome 的上界。实测最亮的深色面是 88（RadioButton 外圈），留 22 级余量。</summary>
    private const int DarkMax = 110;

    /// <summary>浅色前景的下界。实测最暗的前景是 160（下拉框箭头），留 20 级余量。</summary>
    private const int ForegroundMin = 140;

    /// <summary>同一行/同一列上「亮处减暗处」的最小差值：滑块与轨道、文字与底都靠它区分。实测 113~211。</summary>
    private const int SpanMin = 60;

    private readonly UiAppHarness _harness;
    private readonly WireMockServer _wire = WireMockServer.Start();

    public ChromeContrastTests(UiAppHarness harness)
    {
        _harness = harness;
        _wire.Reset();
    }

    public void Dispose() => _wire.Dispose();

    [Fact]
    public async Task DarkChrome_Overview_CardsButtonsLog_AreDark_WithLightText()
    {
        await using var tray = await FakeTrayWithCore();
        using var s = _harness.Launch("--force-dark", "--reset-prefs");
        Assert.True(await tray.WaitUiConnectedAsync(TimeSpan.FromSeconds(10)), "界面没连上假 Tray");
        Show(s, "OverviewPage");
        ScrollToEnd(s);
        Press(s, "LogButton");                                   // 内核输出面板展开，才有第二层底色可取
        await WaitUntilAsync(() => Find(s, "LogText") is not null, 5);

        using var bmp = WindowCapture.Grab(s.Hwnd);
        var win = WindowCapture.WindowRect(s.Hwnd);
        AssertDark(bmp, win, s, "OverviewPage", 0.85, 0.10, "概览页底（卡片外的窗口面）");
        AssertDark(bmp, win, s, "ModeRule", 0.03, 0.50, "RadioButton 外圈");
        AssertDark(bmp, win, s, "UpdateButton", 0.01, 0.50, "Button 左边框/填充");
        AssertLight(bmp, win, s, "UpdateButton", 0.50, 0.50, "Button 文字");
        AssertDark(bmp, win, s, "LogButton", 0.01, 0.50, "「展开内核输出」Button 边框");
        AssertDark(bmp, win, s, "LogText", 0.02, 0.05, "内核输出面板底");
    }

    [Fact]
    public async Task DarkChrome_Settings_ComboBoxEditRadioButtons_AreDark_WithLightText()
    {
        await using var tray = await FakeTrayWithCore();
        using var s = _harness.Launch("--force-dark", "--reset-prefs");
        Assert.True(await tray.WaitUiConnectedAsync(TimeSpan.FromSeconds(10)), "界面没连上假 Tray");
        Show(s, "SettingsPage");
        ScrollToEnd(s);                                          // 材质/主题两卡在页面下半部，不滚就是 0×0

        using var bmp = WindowCapture.Grab(s.Hwnd);
        var win = WindowCapture.WindowRect(s.Hwnd);
        AssertDark(bmp, win, s, "SettingsPage", 0.85, 0.10, "设置页底");
        AssertDark(bmp, win, s, "ImportUrlBox", 0.02, 0.50, "TextBox（导入链接）底");
        AssertDark(bmp, win, s, "ImportButton", 0.01, 0.50, "「导入订阅」Button 边框");
        AssertDark(bmp, win, s, "DelayUrlCombo", 0.01, 0.50, "ComboBox 左边框/填充");
        AssertLight(bmp, win, s, "DelayUrlCombo", 0.50, 0.50, "ComboBox 当前值文字");
        // 箭头落在 fx≈0.94，不是计划里写的 0.92：0.92 实测压在文字与箭头之间的空档上（R-16a 表）。
        AssertLight(bmp, win, s, "DelayUrlCombo", 0.94, 0.50, "ComboBox 下拉箭头");
        AssertDark(bmp, win, s, "BackdropAcrylic", 0.04, 0.50, "材质 RadioButton 外圈");
        AssertDark(bmp, win, s, "ThemeDark", 0.04, 0.50, "主题 RadioButton 外圈");
    }

    [Fact]
    public async Task DarkChrome_ComboBoxItems_AreDarkRows_WithLightText()
    {
        await using var tray = await FakeTrayWithCore();
        using var s = _harness.Launch("--force-dark", "--reset-prefs");
        Assert.True(await tray.WaitUiConnectedAsync(TimeSpan.FromSeconds(10)), "界面没连上假 Tray");
        Show(s, "SettingsPage");
        s.Window.SetForeground();
        var combo = s.Require("DelayUrlCombo").AsComboBox();
        combo.Patterns.ExpandCollapse.Pattern.Expand();
        await WaitUntilAsync(() => Items(s).Length >= 3, 5);

        // PrintWindow 抓不到弹层（实测裁图里只有背后的页面），所以这一类只能从屏幕 DC 取。
        // 代价是它承认遮挡风险：跑这条用例时不许有别家窗口压在界面右上角。
        using var scr = ScreenCapture.Grab();
        var vo = ScreenCapture.VirtualOrigin();
        var origin = new WindowCapture.RECT { Left = vo.X, Top = vo.Y, Right = vo.X + scr.Width, Bottom = vo.Y + scr.Height };

        // 虚拟化的下拉只给**视口内**的行报矩形，其余是 0×0——先按矩形筛掉，否则扫描会当成「取样没跑」直接抛。
        var rows = Items(s).Where(e => e.BoundingRectangle.Width > 0 && e.BoundingRectangle.Height > 0).Take(6).ToList();
        Assert.True(rows.Count >= 3, $"下拉只数到 {rows.Count} 行有几何，ComboBoxItem 这类的核对没跑成");
        var scans = rows.Select(r => ChromeProbe.ScanHorizontal(scr, r.BoundingRectangle, origin, 0.5)).ToList();
        combo.Patterns.ExpandCollapse.Pattern.Collapse();

        // 首行是当前选中项，实测它的矩形与渲染位置错位（横向扫描整行都取不到字），所以判据按
        // 「采到的行里至少 3 行是暗底亮字」——既覆盖到 ComboBoxItem 这一类，又不赌那一条错位。
        var lit = scans.Count(x => x.lo <= DarkMax && x.hi >= ForegroundMin);
        Assert.True(lit >= 3,
            $"ComboBoxItem 里只有 {lit}/{scans.Count} 行同时满足「暗底（≤{DarkMax}）+ 亮字（≥{ForegroundMin}）」——" +
            $"逐行扫描（最暗/最亮）：{string.Join(" ", scans.Select(x => $"{x.lo}/{x.hi}"))}");
    }

    [Fact]
    public async Task DarkChrome_NodeRows_AndGroupRow_AreDark_WithLightText()
    {
        Stub(11);
        await using var tray = await FakeTrayWithCore();
        using var s = _harness.Launch("--force-dark", "--reset-prefs");
        Assert.True(await tray.WaitUiConnectedAsync(TimeSpan.FromSeconds(10)), "界面没连上假 Tray");
        Show(s, "ProxiesPage");
        await WaitUntilAsync(() => Find(s, "Node_n1") is not null, 15);

        using var bmp = WindowCapture.Grab(s.Hwnd);
        var win = WindowCapture.WindowRect(s.Hwnd);
        AssertDark(bmp, win, s, "ProxiesPage", 0.85, 0.10, "节点页底");
        AssertDark(bmp, win, s, "TestAllButton", 0.01, 0.50, "「全组测速」Button 边框");
        AssertLight(bmp, win, s, "TestAllButton", 0.50, 0.50, "「全组测速」Button 文字");
        AssertDark(bmp, win, s, "Group_G", 0.10, 0.50, "组行底");
        AssertDark(bmp, win, s, "Node_n1", 0.50, 0.50, "节点行底（就地更新的那块）");
        AssertLight(bmp, win, s, "Node_n1", 0.03, 0.50, "节点行名字文字");
    }

    [Fact]
    public async Task DarkChrome_ScrollBar_ThumbStandsOutFromTrack_AndRepeatButtonsDark()
    {
        Stub(11);
        await using var tray = await FakeTrayWithCore();
        using var s = _harness.Launch("--force-dark", "--reset-prefs");
        Assert.True(await tray.WaitUiConnectedAsync(TimeSpan.FromSeconds(10)), "界面没连上假 Tray");
        Show(s, "ProxiesPage");
        await WaitUntilAsync(() => Find(s, "Node_n1") is not null, 15);

        var bar = RevealScrollBar(s);
        using var bmp = WindowCapture.Grab(s.Hwnd);
        var win = WindowCapture.WindowRect(s.Hwnd);

        // ① 轨道与滑块：WinUI 的 ScrollBar 在 UIA 里**不暴露 Thumb 元素**（实测部件只有 4 个 RepeatButton），
        //    而且整条 rect 有 15px 宽、滑块只占中间约 6px——所以按「先找最亮列、再沿列竖扫」量（见 ChromeProbe.ScanThumb）。
        //    实测：轨道 48，滑块芯 161，滑块连续约 30 行（60 行采样）。
        var (trackLo, thumbHi, thumbRows) = ChromeProbe.ScanThumb(bmp, bar.BoundingRectangle, win);
        Assert.True(trackLo <= DarkMax, $"滚动条轨道最暗处读到 {trackLo}——应当是暗的（≤{DarkMax}）");
        Assert.True(thumbHi >= ForegroundMin, $"滚动条滑块最亮处读到 {thumbHi}——深色档下滑块必须看得见（≥{ForegroundMin}，实测 161）");
        Assert.True(thumbHi - trackLo >= SpanMin,
            $"滑块与轨道只差 {thumbHi - trackLo}（判据 ≥{SpanMin}）：实测 161-48=113，差太小就是滑块看不见");
        Assert.True(thumbRows >= 6,
            $"滑块只有 {thumbRows} 行达到前景亮度（判据 ≥6，实测约 30）——多半量到的是 1px 抗锯齿边缘，不是滑块");

        // ② 翻页 RepeatButton：滚到顶时 VerticalLargeDecrease 是 0×0（没有剩余空间），所以只判「有几何的那些」。
        var parts = bar.FindAllDescendants().Where(p => p.BoundingRectangle.Width > 0 && p.BoundingRectangle.Height > 0).ToList();
        Assert.True(parts.Count >= 3,
            $"滚动条只数到 {parts.Count} 个有几何的部件（判据 ≥3：两个箭头 + 至少一段翻页）——部件找不到＝不通过");
        foreach (var p in parts)
        {
            var c = ChromeProbe.Sample(bmp, p.BoundingRectangle, win, 0.5, 0.5);
            Assert.True(ChromeProbe.Lum(c) <= DarkMax,
                $"翻页部件 {Id(p)}（{p.ControlType}）读到 {ChromeProbe.Describe(c)}——深色档下应当是暗的（≤{DarkMax}）");
        }
    }

    // ---------- 取样 ----------

    private static void AssertDark(Bitmap bmp, WindowCapture.RECT win, UiSession s,
        string id, double fx, double fy, string what)
        => AssertLum(bmp, win, s, id, fx, fy, what, v => v <= DarkMax, $"≤{DarkMax}");

    private static void AssertLight(Bitmap bmp, WindowCapture.RECT win, UiSession s,
        string id, double fx, double fy, string what)
        => AssertLum(bmp, win, s, id, fx, fy, what, v => v >= ForegroundMin, $"≥{ForegroundMin}");

    private static void AssertLum(Bitmap bmp, WindowCapture.RECT win, UiSession s, string id,
        double fx, double fy, string what, Func<int, bool> ok, string bound)
    {
        var el = Find(s, id);
        Assert.True(el is not null, $"{what}（{id}）在窗口里找不到——这块核对没跑成");
        var r = el!.BoundingRectangle;
        Assert.True(r.Width > 0 && r.Height > 0,
            $"{what}（{id}）矩形为 {r.Width}x{r.Height}——它在视口外，取样等于没跑（先滚到它）");
        var c = ChromeProbe.Sample(bmp, r, win, fx, fy);
        Assert.True(ok(ChromeProbe.Lum(c)),
            $"{what} 在 ({fx},{fy}) 读到 {ChromeProbe.Describe(c)}，判据 {bound} 不成立");
    }

    /// <summary>
    /// 让深色滚动条显形。三条实测换来的手法（R-16e）：
    /// ① 落点要在**条带内**（页面右缘内 2px）——落在右缘内 13px 时指针压在内容上，条不出来；
    /// ② 悬停要先被 UI 线程消化（两次 MoveTo + 停顿），单发 MoveTo 紧接着滚轮经常不生效；
    /// ③ 这条必须在**没碰过 ComboBox** 的干净页面上跑：弹层收起后主窗仍吞掉悬停，滚动条此后再也不显形
    ///    （所以每个 Fact 各起一个窗口，而不是共用一条长流程）。
    /// 拿不到部件直接抛——「找不到部件＝不通过」，不许静默跳过。
    /// </summary>
    private static AutomationElement RevealScrollBar(UiSession s)
    {
        var client = s.Require("ProxiesPage").BoundingRectangle;
        s.Window.SetForeground();
        var pt = new Point(client.Right - 2, client.Top + client.Height / 2);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        var attempts = 0;
        while (DateTime.UtcNow < deadline)
        {
            Mouse.MoveTo(pt);
            Thread.Sleep(700);                                       // 悬停要先被 UI 线程消化，实测 150ms 不够
            Mouse.MoveTo(new Point(pt.X, pt.Y - 2));
            Thread.Sleep(300);
            Mouse.Scroll(-3);
            var bar = s.Window.FindAllDescendants(cf => cf.ByControlType(ControlType.ScrollBar))
                .FirstOrDefault(e => e.BoundingRectangle.Width > 0 && e.BoundingRectangle.Height > 0);
            if (bar is not null) return bar;
            attempts++;
            Thread.Sleep(200);
        }
        throw new Xunit.Sdk.XunitException(
            $"悬停+滚轮 {attempts} 次仍拿不到有几何的 ScrollBar（页面右缘 {client.Right}，落点 {pt}，" +
            $"指针下窗口=0x{WindowFromPoint(new POINT { X = pt.X, Y = pt.Y }).ToInt64():X} 本窗=0x{s.Hwnd.ToInt64():X}，" +
            $"前台={(GetForegroundWindow() == s.Hwnd)}）——滚动条的深色核对没跑成");
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT p);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    // ---------- 夹具 ----------

    private static AutomationElement? Find(UiSession s, string id) =>
        s.Window.FindFirstDescendant(cf => cf.ByAutomationId(id));

    private static AutomationElement[] Items(UiSession s) =>
        s.Window.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem));

    private static string Id(AutomationElement e)
    {
        try { return e.AutomationId; } catch (Exception) { return e.ControlType.ToString(); }
    }

    /// <summary>FlaUI 5 没有 ScrollIntoView，只能拿 ScrollViewer 的 ScrollPattern 按比例滚到底。</summary>
    private static void ScrollToEnd(UiSession s)
    {
        foreach (var e in s.Window.FindAllDescendants())
        {
            try
            {
                if (e.Patterns.Scroll.IsSupported) e.Patterns.Scroll.Pattern.SetScrollPercent(-1, 100);
            }
            catch (Exception) { /* 不是所有 pane 都能滚，试下一个 */ }
        }
        Thread.Sleep(400);
    }

    private static void Show(UiSession s, string pageId)
    {
        var navId = pageId switch
        {
            "ProxiesPage" => "NavProxies",
            "SettingsPage" => "NavSettings",
            _ => "NavOverview",
        };
        s.Require(navId).Patterns.SelectionItem.Pattern.Select();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(8);
        while (DateTime.UtcNow < deadline)
        {
            var page = Find(s, pageId);
            if (page is not null && !page.IsOffscreen) return;
            Thread.Sleep(100);
        }
        throw new Xunit.Sdk.XunitException($"切换后未见页面 {pageId}");
    }

    private static void Press(UiSession s, string id) => s.Require(id).AsButton().Invoke();

    private static async Task WaitUntilAsync(Func<bool> condition, int seconds)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(100);
        }
        throw new Xunit.Sdk.XunitException($"等待超时（{seconds}s）");
    }

    private async Task<FakeTray> FakeTrayWithCore()
    {
        var tray = await FakeTray.StartAsync();
        tray.Handshake = new HandshakeReply(CoreState.Running, "127.0.0.1", _wire.Port, "sec-test",
            18999, "v-test", $"{_wire.Url}/generate_204");
        tray.Snapshot = tray.Snapshot with { DelayTestUrl = $"{_wire.Url}/generate_204" };
        return tray;
    }

    /// <summary>30 个成员的裸 JSON：真机键名带横线，匿名类型写不出来（R-15 同一条理由）。</summary>
    private void Stub(int delay)
    {
        var nodes = string.Join(",", Enumerable.Range(1, 30).Select(i =>
            $"\"n{i}\":{{\"name\":\"n{i}\",\"type\":\"Shadowsocks\",\"hidden\":false,\"provider-name\":\"\",\"delay\":{delay},\"history\":[]}}"));
        var all = string.Join(",", Enumerable.Range(1, 30).Select(i => $"\"n{i}\""));
        _wire.ResetMappings();
        _wire.Given(Request.Create().WithPath("/proxies").UsingGet())
             .RespondWith(Response.Create().WithBody($$"""
             {"proxies":{
               "G":{"name":"G","type":"Selector","now":"n1","all":[{{all}}],"hidden":false,"history":[]},
               {{nodes}}
             }
             }
             """));
        _wire.Given(Request.Create().WithPath("/providers/proxies").UsingGet())
             .RespondWith(Response.Create().WithBody("""
             {"providers":{"default":{"name":"default","vehicleType":"Compatible","updatedAt":"0001-01-01T00:00:00Z","proxies":[]}}}
             """));
    }
}
