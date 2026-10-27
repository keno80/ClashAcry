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
/// 所以每条断言都落在「某个控件的某个区域量出来的亮度分布」上；控件类别按约束清单逐个覆盖：
/// <c>Button</c> / <c>ComboBox</c> / <c>ComboBoxItem</c> / 自绘节点行 / <c>ScrollBar</c>（轨道、滑块、翻页 RepeatButton）。
/// 每条都是**双极性**的：同一块上既要点出「底是暗的」，又要点出「前景（文字/箭头/滑块）是亮的」——
/// 只量暗会把「整屏糊成黑」也判成通过，那正是深色档最常见的假通过形态。
/// 反证在 <see cref="FillCriterion_LightThemeReallyReadsLight"/>：浅色档下同一量法必须读到发白，
/// 否则「≤110」是在自说自话。
/// 阈值来自取样探针的实测（R-16 段 + Task 18 量法表）：深色档底色 P25 落在 48~70、亮特征 max 落在 204~255，
/// 所以 <see cref="DarkMax"/>=110 与 <see cref="ForegroundMin"/>=140 两侧都有余量；跨度实测 140~200，<see cref="SpanMin"/>=60。
/// 三条夹具约定：
/// ① 一律 <c>--force-dark</c>：不写进 <see cref="UiPrefs"/>（那是用户的偏好，测试不许改），只在本次进程里钉深色；
/// ② 取样前先确认元素有非零矩形——WinUI 对**视口外**的元素报 0×0，拿它取样等于这条核对没跑；
/// ③ 弹层（ComboBox 下拉）不在主窗的合成面里，PrintWindow 抓不到，必须走整屏抓取（见 <see cref="ScreenCapture"/>）。
/// 判据形态在 Task 18 从「单点 RGB」改成「区域分位数」，成因见 <see cref="AssertFillDark"/> 的注释。
/// </summary>
public sealed class ChromeContrastTests : IClassFixture<UiAppHarness>, IDisposable
{
    /// <summary>底色（区域 P25）的上界。实测深色档 48~70，浅色档 240 上下，两侧都留了余量。</summary>
    private const int DarkMax = 110;

    /// <summary>亮特征（区域 max）的下界。实测最暗的是下拉箭头波段 204，弹层行按 R-16a 是 160 级。</summary>
    private const int ForegroundMin = 140;

    /// <summary>同一区域里「亮特征减底色」的最小差值：文字、箭头、滑块都靠它区分。实测 140~200，弹层行 113 起。</summary>
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
        WaitLayoutSettled(s, "OverviewPage", "ModeRule", "UpdateButton", "LogButton", "LogText");

        using var bmp = WindowCapture.Grab(s.Hwnd);
        var win = WindowCapture.WindowRect(s.Hwnd);
        AssertFillDark(bmp, win, s, "OverviewPage", "概览页底（卡片外的窗口面）");
        AssertFillDark(bmp, win, s, "ModeRule", "RadioButton 面（外圈与标签底）");
        AssertFillDark(bmp, win, s, "UpdateButton", "Button 面（边框与填充）");
        AssertFeatureLight(bmp, win, s, "UpdateButton", "Button 文字");
        AssertFillDark(bmp, win, s, "LogButton", "「展开内核输出」Button 面");
        AssertFillDark(bmp, win, s, "LogText", "内核输出面板底");
    }

    [Fact]
    public async Task DarkChrome_Settings_ComboBoxEditRadioButtons_AreDark_WithLightText()
    {
        await using var tray = await FakeTrayWithCore();
        using var s = _harness.Launch("--force-dark", "--reset-prefs");
        Assert.True(await tray.WaitUiConnectedAsync(TimeSpan.FromSeconds(10)), "界面没连上假 Tray");
        Show(s, "SettingsPage");
        ScrollToEnd(s);                                          // 材质/主题两卡在页面下半部，不滚就是 0×0
        WaitLayoutSettled(s, "SettingsPage", "ImportUrlBox", "ImportButton", "DelayUrlCombo", "ThemeDark");

        using var bmp = WindowCapture.Grab(s.Hwnd);
        var win = WindowCapture.WindowRect(s.Hwnd);
        AssertFillDark(bmp, win, s, "SettingsPage", "设置页底");
        AssertFillDark(bmp, win, s, "ImportUrlBox", "TextBox（导入链接）面：含标签行与占位符行");
        AssertFillDark(bmp, win, s, "ImportButton", "「导入订阅」Button 面");
        AssertFillDark(bmp, win, s, "DelayUrlCombo", "ComboBox 面（边框与填充）");
        AssertFeatureLight(bmp, win, s, "DelayUrlCombo", "ComboBox 当前值文字", 0.0, 0.80);
        AssertFeatureLight(bmp, win, s, "DelayUrlCombo", "ComboBox 下拉箭头", 0.80, 1.0);
        AssertFillDark(bmp, win, s, "BackdropAcrylic", "材质 RadioButton 面");
        AssertFillDark(bmp, win, s, "ThemeDark", "主题 RadioButton 面");
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
        WaitLayoutSettled(s, "ProxiesPage", "TestAllButton", "Group_G", "Node_n1");

        using var bmp = WindowCapture.Grab(s.Hwnd);
        var win = WindowCapture.WindowRect(s.Hwnd);
        AssertFillDark(bmp, win, s, "ProxiesPage", "节点页底");
        AssertFillDark(bmp, win, s, "TestAllButton", "「全部测速」Button 面");
        AssertFeatureLight(bmp, win, s, "TestAllButton", "「全部测速」Button 文字");
        AssertFillDark(bmp, win, s, "Group_G", "组行那一行的底");
        AssertFillDark(bmp, win, s, "Node_n1", "节点行底（就地更新的那块）");
        AssertFeatureLight(bmp, win, s, "Node_n1", "节点行名字文字", 0.0, 0.25);
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

        // 悬停是展开态的前提：指针一挪开条就收回 3px 药丸，所以取样全过程都在悬停里，收尾必须把指针还回原位。
        var origin = CursorPos();
        try
        {
            var bar = RevealScrollBar(s);
            using var bmp = WindowCapture.Grab(s.Hwnd);
            var win = WindowCapture.WindowRect(s.Hwnd);

            // ① 轨道与滑块：WinUI 的 ScrollBar 在 UIA 里**不暴露 Thumb 元素**（实测部件只有 4 个 RepeatButton），
            //    而且整条 rect 有 15px 宽、滑块只占中间约 6px——所以按「先找最亮列、再沿列竖扫」量（见 ChromeProbe.ScanThumb）。
            //    实测：轨道 48，滑块芯 161，滑块连续约 30 行（60 行采样）。
            var (trackLo, thumbHi, thumbRows) = ChromeProbe.ScanThumb(bmp, bar.BoundingRectangle, win);
            Assert.True(trackLo <= DarkMax,
                $"滚动条轨道最暗处读到 {trackLo}——应当是暗的（≤{DarkMax}；条矩形 {bar.BoundingRectangle}，滑块行 {thumbRows}）");
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
        finally
        {
            SetCursorPos(origin.X, origin.Y);
        }
    }

    /// <summary>
    /// 反证（Task 18 补）：把主题真的切到浅色，同一套区域量法必须读到「底发白」。
    /// 没有这一条，「≤110」只是在自说自话——单点取样时代没法这样核对，因为那个点本身就不稳。
    /// 收尾切回「跟随系统」：主题单选会把偏好落进 LocalSettings，那是用户的东西，测试不许给他留在浅色上。
    /// </summary>
    [Fact]
    public async Task FillCriterion_LightThemeReallyReadsLight()
    {
        await using var tray = await FakeTrayWithCore();
        using var s = _harness.Launch("--reset-prefs");          // 不钉深色：让主题单选真的生效
        Assert.True(await tray.WaitUiConnectedAsync(TimeSpan.FromSeconds(10)), "界面没连上假 Tray");
        Show(s, "SettingsPage");
        ScrollToEnd(s);
        Select(s, "ThemeLight");
        WaitLayoutSettled(s, "SettingsPage", "ImportUrlBox", "ImportButton");
        try
        {
            using var bmp = WindowCapture.Grab(s.Hwnd);
            var win = WindowCapture.WindowRect(s.Hwnd);
            var (pageP25, _, _) = Stats(bmp, win, s, "SettingsPage", "设置页底（浅色档）");
            var (boxP25, _, _) = Stats(bmp, win, s, "ImportUrlBox", "TextBox 面（浅色档）");
            Assert.True(pageP25 > DarkMax && boxP25 > DarkMax,
                $"浅色档下底色 P25 仍读到 页面 {pageP25} / 输入框 {boxP25}（判据 >{DarkMax}）" +
                "——深色核对的判据没有区分力，是夹具坏了，不是界面没问题");
        }
        finally
        {
            Select(s, "ThemeSystem");
        }
    }

    // ---------- 取样 ----------

    /// <summary>
    /// 「这块面在深色档里是暗的」＝元素区域的**底色分位数**（P25）够暗。
    /// 实测深色档 48~70，浅色档（反证用例）240+，判据线 110 两侧都有余量。
    /// 不再用单点：Task 18 全量套件里两条随机失败都是单点踩偏——ImportUrlBox 的占位符字脊实测 238、
    /// 字段底 35，两者只隔 5 px；画面相对 UIA 矩形漂移几个像素就把「判底」变成「判字」。
    /// </summary>
    private static void AssertFillDark(Bitmap bmp, WindowCapture.RECT win, UiSession s, string id, string what,
        double xFrom = 0, double xTo = 1)
    {
        var (p25, max, min) = Stats(bmp, win, s, id, what, xFrom, xTo);
        Assert.True(p25 <= DarkMax,
            $"{what}（{id}）底色 P25={p25}，判据 ≤{DarkMax} 不成立（同区域 min/max={min}/{max}）——深色档下这块面发白了");
    }

    /// <summary>
    /// 「这个亮特征在深色里分得开」＝同一段区域里既有够亮的前景特征（max），又确实坐在暗底上（P25），
    /// 且两者跨度够大。极性与跨度一起判，沿用 R-16 的口径：只量暗会把整屏糊成黑也判成通过。
    /// </summary>
    private static void AssertFeatureLight(Bitmap bmp, WindowCapture.RECT win, UiSession s, string id, string what,
        double xFrom = 0, double xTo = 1)
    {
        var (p25, max, min) = Stats(bmp, win, s, id, what, xFrom, xTo);
        Assert.True(max >= ForegroundMin,
            $"{what}（{id}）波段 [{xFrom:0.00},{xTo:0.00}] 最亮只到 {max}，判据 ≥{ForegroundMin} 不成立（底色 P25={p25}，min={min}）" +
            "——这个特征在深色里看不见");
        Assert.True(max - p25 >= SpanMin,
            $"{what}（{id}）亮特征 {max} 与底色 {p25} 只差 {max - p25}（判据 ≥{SpanMin}）——糊成一片了");
    }

    private static (int p25, int max, int min) Stats(Bitmap bmp, WindowCapture.RECT win, UiSession s,
        string id, string what, double xFrom = 0, double xTo = 1)
    {
        var el = Find(s, id);
        Assert.True(el is not null, $"{what}（{id}）在窗口里找不到——这块核对没跑成");
        var r = el!.BoundingRectangle;
        Assert.True(r.Width > 0 && r.Height > 0,
            $"{what}（{id}）矩形为 {r.Width}x{r.Height}——它在视口外，取样等于没跑（先滚到它）");
        return ChromeProbe.BandStats(bmp, r, win, xFrom, xTo);
    }

    /// <summary>
    /// 抓帧前等布局定下来：连续三次读到同一组矩形（100 ms 一次）再多让一帧。
    /// 位图与 UIA 矩形本来就读于两个时刻，页面刚切换/刚滚完时它真的还在漂（Task 18 那两条随机失败的成因）；
    /// 收不拢也不阻塞——判据本身已经改成区域统计，几像素的漂移撼不动结论。
    /// </summary>
    private static void WaitLayoutSettled(UiSession s, params string[] ids)
    {
        var last = "";
        var stable = 0;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            var now = string.Join("|", ids.Select(id => Find(s, id)?.BoundingRectangle.ToString() ?? "-"));
            stable = now == last ? stable + 1 : 0;
            last = now;
            if (stable >= 2)
            {
                Thread.Sleep(120);
                return;
            }
            Thread.Sleep(100);
        }
    }

    private static void Select(UiSession s, string automationId) =>
        s.Require(automationId).Patterns.SelectionItem.Pattern.Select();

    /// <summary>
    /// 让滚动条显形并**展开**成能量轨道/翻页件的形态。三步全是本机实测换来的（Task 18）：
    /// ① 程序化滚动（<c>ScrollPattern.SetScrollPercent</c>）让条存在——不滚时它压根没有几何；
    ///    此时是紧凑态：实测 3px 宽、整列 161、UIA 部件为**空**（没有轨道也没有翻页件），
    ///    拿它当「轨道 vs 滑块」量就是在量滑块本身，于是「轨道最暗处读到 161」；
    /// ② 紧凑态约 1s 后自己收回 0×0，所以指针落点必须在滚完之后**立刻**按下——
    ///    先滚完再等 1s 才动指针，就变成在空气上悬停（实测落点算出 y=0 就是这么来的）；
    /// ③ 展开到实测 15px（轨道 + 4 个 RepeatButton）只在**指针压在条上并且真有滚轮进来**时发生：
    ///    光 <c>SetCursorPos</c> 悬停、光程序化滚动都停在 3px 药丸，试了 12 轮 10 个落点都不展开。
    ///    滚轮是打到指针下的窗口的，所以发之前先 <c>WindowFromPoint</c> + 属主 PID 验明那正是本测试起的
    ///    UI 进程——不是它就一个字节都不发。这条护栏是原来那套「悬停+滚轮」缺的（失败现场：指针下窗口=别家 HWND、
    ///    前台=False，滚轮滚到了用户别的界面上）。
    /// 展开失败直接抛，并把落点归属、条矩形、部件数一起打出来——「量不到轨道＝不通过」，不许静默降级成只判那颗药丸。
    /// </summary>
    private static AutomationElement RevealScrollBar(UiSession s)
    {
        var log = new List<string>();
        var page = s.Require("ProxiesPage").BoundingRectangle;
        for (var round = 0; round < 12; round++)
        {
            // 每轮重新滚一次：滚动让条出现（约 1s 后收回 0×0），落点必须按当下还活着的药丸算
            var bar = CompactScrollBar(s);
            var r = bar.BoundingRectangle;
            var x = r.X + r.Width / 2;
            var y = r.Top + r.Height / 2;
            var under = WindowFromPoint(new POINT { X = x, Y = y });
            GetWindowThreadProcessId(under, out var underPid);
            var ours = underPid == (uint)s.Process.Id;
            for (var k = 0; k < 6 && ours; k++)
            {
                SetCursorPos(x, y - k % 2);                            // 抖 1px 让 XAML 收到 pointer move
                // 展开态实测只在「指针压在条上 + 真滚轮进来」时出现（光悬停、光程序化滚动都停在 3px 药丸）。
                // 滚轮打在指针下的窗口上，所以先验明那正是本测试起的 UI 进程（ours），否则一个字节都不发。
                Mouse.Scroll(-1);
                Thread.Sleep(220);
                var expanded = s.Window.FindAllDescendants(cf => cf.ByControlType(ControlType.ScrollBar))
                    .Where(e => e.BoundingRectangle.Width >= ExpandedMinWidth && e.BoundingRectangle.Height > 0)
                    .OrderByDescending(e => e.BoundingRectangle.Height)
                    .FirstOrDefault();
                if (expanded is not null) return expanded;
            }
            var now = s.Window.FindAllDescendants(cf => cf.ByControlType(ControlType.ScrollBar))
                .OrderByDescending(e => e.BoundingRectangle.Height).FirstOrDefault();
            var nr = now?.BoundingRectangle ?? Rectangle.Empty;
            log.Add($"r{round} 落点({x},{y}) 页右缘-{page.Right - x}: {nr.Width}x{nr.Height}@({nr.X},{nr.Y}) " +
                    $"部件{(now?.FindAllDescendants().Length ?? -1)} 指针下=0x{under.ToInt64():X}{HwndInfo(under)} 属主pid={underPid}");
        }
        var win0 = WindowCapture.WindowRect(s.Hwnd);
        throw new Xunit.Sdk.XunitException(
            $"滚动+悬停+滚轮 12 轮后滚动条仍未展开到 ≥{ExpandedMinWidth}px（落点不属本进程时滚轮根本不发）" +
            $"（本窗=0x{s.Hwnd.ToInt64():X}，前台={(GetForegroundWindow() == s.Hwnd)}，" +
            $"页 {page}，窗 ({win0.Left},{win0.Top},{win0.Right},{win0.Bottom})）|| {string.Join(" | ", log)}");
    }

    /// <summary>程序化滚动到条显形（紧凑态：实测 3px 宽、只有滑块药丸）。</summary>
    private static AutomationElement CompactScrollBar(UiSession s)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        var attempts = 0;
        while (DateTime.UtcNow < deadline)
        {
            foreach (var e in s.Window.FindAllDescendants())
            {
                try
                {
                    if (e.Patterns.Scroll.IsSupported) e.Patterns.Scroll.Pattern.SetScrollPercent(-1, 25 + (attempts % 6) * 8);
                }
                catch (Exception) { /* 有的 pane 不吃横向 -1 或还没就绪，试下一个 */ }
            }
            var bar = s.Window.FindAllDescendants(cf => cf.ByControlType(ControlType.ScrollBar))
                .Where(e =>
                {
                    var r = e.BoundingRectangle;
                    return r.Width > 0 && r.Height > 0 && r.Height > r.Width;
                })
                .OrderByDescending(e => e.BoundingRectangle.Height)
                .FirstOrDefault();
            if (bar is not null) return bar;
            attempts++;
            Thread.Sleep(120);
        }
        throw new Xunit.Sdk.XunitException(
            $"程序化滚动 {attempts} 次仍拿不到有几何的 ScrollBar（前台={(GetForegroundWindow() == s.Hwnd)}）" +
            "——滚动条的深色核对没跑成");
    }

    private const int ExpandedMinWidth = 12;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT p);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT p);

    private static POINT CursorPos()
    {
        GetCursorPos(out var p);
        return p;
    }

    /// <summary>落点归属取证：类名 + 属主进程——「指针下窗口≠本窗」有两种解释（别家压在上面 / 自己岛的子窗口），结论完全不同。</summary>
    private static string HwndInfo(IntPtr h)
    {
        if (h == IntPtr.Zero) return "(空)";
        var sb = new System.Text.StringBuilder(256);
        GetClassName(h, sb, sb.Capacity);
        GetWindowThreadProcessId(h, out var pid);
        return $"[{sb}|pid={pid}]";
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetClassName(IntPtr h, System.Text.StringBuilder buf, int max);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

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
