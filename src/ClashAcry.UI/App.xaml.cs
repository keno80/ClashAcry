using System.IO;
using ClashAcry.Core;
using ClashAcry.UI.Services;
using Microsoft.UI.Xaml;

namespace ClashAcry.UI;

/// <summary>
/// UI 进程的生命周期入口（spec §3：关窗即退，UI 不常驻、不碰内核进程）。
/// 三条纪律：
/// ① 单实例闸门在**建窗之前**判——第二份 UI 意味着两个渲染面读同一个 handshake，
///    最坏是用户在两个窗口里对同一个内核下相反的命令；
/// ② 未处理异常必须落盘（Step 2 第 3 条）：WinUI 的 XAML 异常默认会被运行时咽成一次渲染降级，
///    用户看到的是"界面不对"而不是"崩了"，没有日志就永远查不动；
/// ③ 这里只允许起 Tray（<see cref="TrayLink.EnsureTrayRunningAsync"/>），绝不起 mihomo。
/// </summary>
public partial class App : Application
{
    private MainWindow? _window;

    /// <summary>测试启动参数 <c>--tray-absent</c>：跳过拉起、直接走「托盘宿主未运行」那条分支（Step 4 的断言面）。
    /// 这条分支现在是**有终点的**：横幅先说一句为什么，宽限一尽界面自己退（<see cref="MainWindow"/> 里那条
    /// <c>Overview.TrayLost</c>），不再留一个"看着还在、其实什么都做不了"的窗口（用户裁定 2026-10-28）。</summary>
    public static bool TrayAbsentRequested { get; private set; }

    /// <summary><c>--reset-prefs</c>：把 LocalSettings 里的界面偏好清空，回到「全新安装」的状态。
    /// 没有它，上一条用例写进去的 Mica 会让「默认档是亚克力」那条用例永远读不到默认值。</summary>
    public static bool ResetPrefsRequested { get; private set; }

    /// <summary><c>--force-degrade</c>：把「系统允许透明效果」注入成假。降级那条分支真机上要靠改用户的
    /// 个性化设置才能复现（R-13a：测试不改别人的机器），所以走 <see cref="BackdropService"/> 的注入点。</summary>
    public static bool ForceDegradeRequested { get; private set; }

    /// <summary><c>--force-dark</c>：把内容面钉在深色，让像素取样不受系统主题影响。
    /// <b>不写 <see cref="UiPrefs"/></b>——写进去就等于用一条测试参数改了用户的界面偏好，
    /// 下一条读默认档的用例会被它污染（R-13d 同一类地雷）。</summary>
    public static bool ForceDarkRequested { get; private set; }

    /// <summary>命令行参数的唯一读取口。unpackaged 下 <c>LaunchActivatedEventArgs.Arguments</c> 是应用激活用的，
    /// 从 <c>ProcessStartInfo</c> 传进来的裸参数只能从 <c>Environment.GetCommandLineArgs()</c> 拿（Step 4 实测）。</summary>
    public static bool HasArg(string name) =>
        Environment.GetCommandLineArgs().Skip(1).Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        TrayAbsentRequested = HasArg("--tray-absent");
        ResetPrefsRequested = HasArg("--reset-prefs");
        ForceDegradeRequested = HasArg("--force-degrade");
        ForceDarkRequested = HasArg("--force-dark");
        UnhandledException += OnUnhandled;

        if (!SingleInstance.TryAcquire())
        {
            SingleInstance.AskExistingUiToForeground();
            Exit();
            return;
        }

        _window = new MainWindow();
        _window.Closed += (_, _) => Exit();     // spec §3：关窗即退进程，不做「隐藏到托盘」的分支
        _window.Activate();
    }

    private void OnUnhandled(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        // 落盘 + 行内反馈，不弹阻塞框（用户红线：长反馈一律走界面，不走对话框）。
        try
        {
            var paths = AppPaths.Default();
            paths.EnsureCreated();
            File.AppendAllText(Path.Combine(paths.LogsDir, $"ui-crash-{DateTime.Now:yyyyMMdd}.log"),
                $"{DateTime.Now:o} {e.Exception}\r\n\r\n");
        }
        catch (Exception) { /* 日志写不下去也不能把崩溃处理本身再崩一次 */ }

        _window?.ShowBanner($"界面异常：{e.Exception.Message}（详情见 logs\\ui-crash-*.log）");
        e.Handled = true;   // 单个渲染分支的异常不该带走整个窗口
    }
}
