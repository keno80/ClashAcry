using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ClashAcry.UI.Services;

/// <summary>
/// 单实例闸门（spec §3：同一个界面只允许存在一份）。
/// 裁定 R-12c：计划里写的是「第二实例发 <c>ui.focus</c>」，但 Tray 侧 <c>ui.focus</c> 的语义是
/// 「UI 不在就起进程、已在就不动」（<c>TrayHost.FocusUi</c> 的 R-10e 注释），第二实例发过去正好落进
/// 「已在就不动」那条空分支，用户什么都看不到。所以这里自己把已有窗口带到前台——
/// 跨进程取名 + 窗口句柄枚举是 Win32 的常规做法，且不开新的跨进程命令面。
/// 互斥体句柄必须一直攥在静态字段上：中途被终结器回收会「遗弃」互斥体，
/// 下一个实例拿到的就是 <see cref="AbandonedMutexException"/> 而不是干净的 false。
/// </summary>
public static class SingleInstance
{
    private const string MutexName = @"Local\ClashAcry.UI.v1";
    private const string AppProcessName = "ClashAcry.UI";

    private static Mutex? _mutex;

    /// <summary>抢到 = 本进程是唯一实例，可以建窗；没抢到 = 已有 UI 在跑，调用方发完前台请求就退。</summary>
    public static bool TryAcquire()
    {
        _mutex = new Mutex(true, MutexName, out var firstInstance);
        return firstInstance;
    }

    /// <summary>把已在跑的那份 UI 主窗口恢复并带到前台。找不到（对方窗口还没建起来）就静默返回。</summary>
    public static void AskExistingUiToForeground()
    {
        foreach (var p in Process.GetProcessesByName(AppProcessName))
        {
            using (p)
            {
                if (p.Id == Environment.ProcessId) continue;
                var h = p.MainWindowHandle;
                if (h == IntPtr.Zero) continue;
                _ = ShowWindow(h, SwRestore);
                _ = SetForegroundWindow(h);
                return;
            }
        }
    }

    private const int SwRestore = 9;

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
