using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using FlaUI.Core.AutomationElements;

namespace ClashAcry.UI.Tests.Fakes;

/// <summary>
/// 窗口像素取样器。判据是像素而不是「肉眼看整体截图」，所以必须能在测试里确定性地把某个控件的某个点读成 RGB。
/// 两条本机约束决定了实现形态：
/// ① WinUI 3 的内容由 DWM 合成，<c>CopyFromScreen</c> 会受遮挡、且抓不到滚动条那几像素宽的部件 →
///    只有 <c>PrintWindow</c> 带 <c>PW_RENDERFULLCONTENT</c> 能把合成面完整渲进位图；
/// ② UIA 的 <c>BoundingRectangle</c> 是**屏幕**坐标，位图是**窗口**坐标 → 必须减窗口左上角换算，
///    这一步错了整份断言集体错位（所以 <see cref="ChromeProbe.Sample"/> 的签名里有 <c>window</c>）。
/// </summary>
public static class WindowCapture
{
    public const uint PW_RENDERFULLCONTENT = 2;

    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
    }

    public static RECT WindowRect(IntPtr hwnd)
    {
        Assert.True(GetWindowRect(hwnd, out var r), "GetWindowRect 失败");
        return r;
    }

    /// <summary>抓整窗（含非客户区）。位图像素 (0,0) 对应 <see cref="WindowRect"/> 的左上角。</summary>
    public static Bitmap Grab(IntPtr hwnd)
    {
        var r = WindowRect(hwnd);
        Assert.True(r.Width > 50 && r.Height > 50, $"窗口矩形不合理：{r.Width}x{r.Height}（被最小化了？）");
        var bmp = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        var hdc = g.GetHdc();
        try { Assert.True(PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT), "PrintWindow 失败：窗口可能被最小化"); }
        finally { g.ReleaseHdc(); }
        return bmp;
    }

    /// <summary>FlaUI 5 的 <see cref="Window"/> 没有 <c>Handle</c> 属性，HWND 只从 <c>Properties.NativeWindowHandle</c> 拿。</summary>
    public static IntPtr Hwnd(Window w) => new(w.Properties.NativeWindowHandle.Value);
}

/// <summary>
/// 整屏抓取。<see cref="WindowCapture.Grab"/> 的 PrintWindow 只渲主窗自己的合成面，
/// WinUI 的 Popup（ComboBox 下拉、Flyout）落在独立的 site bridge 里，抓出来是背后的页面——
/// 所以弹层类控件（ComboBoxItem 等）只能从屏幕 DC 取。判据仍要求屏幕坐标来自 UIA、换算为 0 偏移。
/// </summary>
public static class ScreenCapture
{
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("gdi32.dll")] private static extern int GetDeviceCaps(IntPtr hdc, int nIndex);

    [DllImport("user32.dll")] private static extern void ReleaseDC(IntPtr hwnd, IntPtr hdc);

    private const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;
    private const int DESKTOPHORZRES = 8;                                  // 物理横向分辨率（不受 DPI 虚拟化影响）

    /// <summary>
    /// 报告坐标 ÷ 物理坐标。DPI 档由 <see cref="DpiPrelude"/> 在模块初始化时提到 per-monitor v2，正常应为 1；
    /// 不是 1 就说明提档没成（进程更早的地方已经定过档），此时 UIA 矩形与抓取位图对不上，判据不可信 —— 直接失败。
    /// </summary>
    public static double Scale
    {
        get
        {
            var hdc = GetDC(IntPtr.Zero);
            try
            {
                var virt = GetSystemMetrics(0 /* SM_CXSCREEN */);
                var phys = GetDeviceCaps(hdc, DESKTOPHORZRES);
                return virt <= 0 ? 1 : (double)phys / virt;
            }
            finally { ReleaseDC(IntPtr.Zero, hdc); }
        }
    }

    public static Bitmap Grab()
    {
        Assert.True(Math.Abs(Scale - 1) < 0.01,
            $"屏幕抓取按 1:1 物理坐标取样，当前缩放比 {Scale:F2}——UIA 矩形与位图对不上，弹层核对不可信");
        var x = GetSystemMetrics(SM_XVIRTUALSCREEN);
        var y = GetSystemMetrics(SM_YVIRTUALSCREEN);
        var w = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        var h = GetSystemMetrics(SM_CYVIRTUALSCREEN);
        Assert.True(w > 50 && h > 50, $"虚拟屏幕尺寸不合理：{w}x{h}");
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(x, y, 0, 0, new Size(w, h));
        return bmp;
    }

    /// <summary>抓弹层所在的那块屏幕区域（屏幕坐标 → 以虚拟屏左上为原点的位图坐标）。</summary>
    public static Rectangle VirtualOrigin() =>
        new(GetSystemMetrics(SM_XVIRTUALSCREEN), GetSystemMetrics(SM_YVIRTUALSCREEN), 0, 0);
}

public static class ChromeProbe
{
    /// <summary>
    /// 取控件矩形内的相对落点（<paramref name="fx"/>/<paramref name="fy"/> 为 0~1），返回其 3x3 邻域里
    /// **最不透明且最亮**的那个像素。取「最亮」而不是「中心」：中心可能落在文字或空隙上，而浅色 chrome
    /// 恰恰体现在边框/槽/滑块那一条线上。
    /// </summary>
    public static Color Sample(Bitmap bmp, Rectangle element, WindowCapture.RECT window, double fx, double fy)
    {
        Assert.True(element.Width > 0 && element.Height > 0,
            $"控件矩形为空（{element}）——它多半没在屏幕上，取样等于没跑");
        var x = (int)(element.Width * fx) + element.Left - window.Left;
        var y = (int)(element.Height * fy) + element.Top - window.Top;
        Assert.InRange(x, 0, bmp.Width - 1);
        Assert.InRange(y, 0, bmp.Height - 1);

        Color best = Color.Transparent;
        long bestScore = long.MinValue;
        for (var dy = -1; dy <= 1; dy++)
            for (var dx = -1; dx <= 1; dx++)
            {
                var p = bmp.GetPixel(Math.Clamp(x + dx, 0, bmp.Width - 1), Math.Clamp(y + dy, 0, bmp.Height - 1));
                // 全透明像素是窗口外/未渲染区，亮度按 0 算会被判成「深色」——那是假通过，必须排到最末。
                long score = p.A == 0 ? long.MinValue : p.R + p.G + p.B;
                if (score > bestScore) { bestScore = score; best = p; }
            }
        Assert.True(best.A != 0, $"落点 ({x},{y}) 取到全透明像素——换算或渲染有问题，这块核对没跑成");
        return best;
    }

    /// <summary>
    /// 元素（或其中一段 x 波段）的**亮度分布**：返回「底」（第 25 百分位）、「前景特征」（最大值）与最小值。
    /// 为什么不用单点：控件矩形里文字带与底色带往往只隔几个像素——实测 ImportUrlBox 的占位符字脊 238、
    /// 字段底 35，两者相隔 5 px；节点行名字的字脊宽只有 7 px。页面刚切换/刚滚完时画面还会相对 UIA 矩形
    /// 漂移几像素，于是同一条单点断言一会儿读到底、一会儿读到字（Task 18 全量套件里随机翻脸的两条就是它）。
    /// 分位数把「底」定义成**区域里的主导面**（几个字脊撼不动），把「前景」定义成**该区域里最亮的那一下**，
    /// 两条合起来仍然是「暗底 + 亮特征」的极性判据，只是不再赌某一个像素。
    /// x 方向步长固定 1（箭头/圈这类特征只有 2~3 px 宽，跨步会整个漏掉）；y 方向按元素高度收，
    /// 整页那种 800 px 高的大块取样点数控制在 ~200 行以内。
    /// </summary>
    public static (int p25, int max, int min) BandStats(Bitmap bmp, Rectangle element, WindowCapture.RECT window,
        double xFrom = 0, double xTo = 1)
    {
        Assert.True(element.Width > 0 && element.Height > 0,
            $"控件矩形为空（{element}）——它多半没在屏幕上，取样等于没跑");
        var x0 = Math.Clamp((int)(element.Width * xFrom), 0, element.Width - 1);
        var x1 = Math.Clamp(Math.Max(x0 + 1, (int)(element.Width * xTo)), x0 + 1, element.Width);
        var yStep = Math.Max(1, element.Height / 200);
        var lums = new List<int>(x1 - x0);
        var max = int.MinValue;
        var min = int.MaxValue;
        for (var y = 0; y < element.Height; y += yStep)
        {
            var px = element.Left - window.Left + x0;
            var py = element.Top - window.Top + y;
            Assert.InRange(px, 0, bmp.Width - 1);
            Assert.InRange(py, 0, bmp.Height - 1);
            for (var x = x0; x < x1; x++, px++)
            {
                var c = bmp.GetPixel(px, py);
                // 全透明＝没渲染，判暗会假通过，必须当场失败（同 Sample 的口径）。
                Assert.True(c.A != 0, $"窗口坐标 ({px},{py}) 取到全透明像素——换算或渲染有问题，这块核对没跑成");
                var lum = Lum(c);
                lums.Add(lum);
                max = Math.Max(max, lum);
                min = Math.Min(min, lum);
            }
        }
        lums.Sort();
        return (lums[(int)(lums.Count * 0.25)], max, min);
    }

    public static string Describe(Color c) => $"RGBA({c.R},{c.G},{c.B},{c.A})";

    /// <summary>灰度取三通道最大值：判「暗」要按最亮的那一路，否则纯红/纯绿会蒙混过关。</summary>
    public static int Lum(Color c) => Math.Max(c.R, Math.Max(c.G, c.B));

    /// <summary>沿元素水平方向取 <paramref name="steps"/> 列，返回最暗/最亮灰度（文字行＝暗底＋亮字）。</summary>
    public static (int lo, int hi) ScanHorizontal(Bitmap bmp, Rectangle element, WindowCapture.RECT window,
        double fy, int steps = 40)
    {
        int lo = int.MaxValue, hi = int.MinValue;
        for (var k = 0; k < steps; k++)
        {
            var v = Lum(Sample(bmp, element, window, k / (double)(steps - 1), fy));
            lo = Math.Min(lo, v); hi = Math.Max(hi, v);
        }
        return (lo, hi);
    }

    /// <summary>沿元素竖直方向取 <paramref name="steps"/> 行——滚动条用它一次同时量出轨道与滑块。</summary>
    public static (int lo, int hi) ScanVertical(Bitmap bmp, Rectangle element, WindowCapture.RECT window,
        double fx = 0.5, int steps = 40)
    {
        int lo = int.MaxValue, hi = int.MinValue;
        for (var k = 0; k < steps; k++)
        {
            var v = Lum(Sample(bmp, element, window, fx, k / (double)(steps - 1)));
            lo = Math.Min(lo, v); hi = Math.Max(hi, v);
        }
        return (lo, hi);
    }

    /// <summary>
    /// 滚动条滑块量法。UIA 只给整条（实测 15px 宽）的 rect，而滑块只占中间约 6px：
    /// 横向剖面是 48 48 48 48 48 48 48 62 118 <b>161 161 161</b> 118 62 48——fx=0.5 落在半透明边缘（62），
    /// 所以先粗扫 <paramref name="columns"/> 列找出最亮的那一列，再沿该列竖向量轨道（最暗）、滑块芯（最亮）
    /// 和滑块占的行数，免得把「列选歪了」误判成「滑块看不见」。
    /// </summary>
    public static (int trackLo, int thumbHi, int thumbRows) ScanThumb(Bitmap bmp, Rectangle element,
        WindowCapture.RECT window, int columns = 15, int thumbMin = 140)
    {
        int bestCol = 0, bestHi = int.MinValue;
        for (var c = 0; c < columns; c++)
        {
            var (_, hi) = ScanVertical(bmp, element, window, c / (double)(columns - 1), steps: 20);
            if (hi > bestHi) { bestHi = hi; bestCol = c; }
        }
        var (trackLo, thumbHi) = ScanVertical(bmp, element, window, bestCol / (double)(columns - 1), steps: 60);
        var thumbRows = 0;
        for (var r = 0; r < 60; r++)
            if (Lum(Sample(bmp, element, window, bestCol / (double)(columns - 1), r / 59.0)) >= thumbMin) thumbRows++;
        return (trackLo, thumbHi, thumbRows);
    }
}
