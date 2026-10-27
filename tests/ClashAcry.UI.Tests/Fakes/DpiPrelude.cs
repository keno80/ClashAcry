using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ClashAcry.UI.Tests.Fakes;

/// <summary>
/// 像素取样的前置条件：把**测试进程**提成 per-monitor v2 DPI 档。
/// 本机实测 125% 缩放（物理 2560x1440，unaware 进程看到 2048x1152）：
/// 不提档时 <c>CopyFromScreen</c> 拿到的是缩放后的图，而 UIA 的 <c>BoundingRectangle</c> 报物理坐标，
/// 于是下拉弹层前三行按 UIA 矩形取样全是无字暗区（实测偏移正好 1.25 倍）。
/// 必须走 <see cref="ModuleInitializerAttribute"/>：DPI 档只在进程第一次被查询/建窗前可改，
/// 放在夹具的静态构造里会**中途翻档**——前面几条用例已经按虚拟坐标跑过，后面的就集体错位。
/// </summary>
internal static class DpiPrelude
{
    private static readonly IntPtr DPI_PER_MONITOR_AWARE_V2 = new(-4);

    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [ModuleInitializer]
    internal static void RaiseAwareness()
    {
        try { SetProcessDpiAwarenessContext(DPI_PER_MONITOR_AWARE_V2); }
        catch (Exception) { /* 提不动就由 ScreenCapture.Scale 把不一致暴露成失败 */ }
    }
}
