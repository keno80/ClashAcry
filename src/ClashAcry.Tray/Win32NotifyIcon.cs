using System.Runtime.InteropServices;

namespace ClashAcry.Tray;

/// <summary>
/// 纯 Win32 托盘（spec §2「零 XAML / 不用 WinForms 托盘」，AGENTS.md 硬约束：Tray 常驻 ≤40 MB）。
/// 图标从**磁盘上的 .ico** 经 <c>LoadImageW(LR_LOADFROMFILE)</c> 取 HICON——不用 System.Drawing
/// （裁定 R-10d⑤：System.Drawing 只允许出现在 scripts/make-icon.ps1 里）。
/// 消息窗口用 <c>HWND_MESSAGE</c>：它不占任务栏、不画东西，只承接托盘回调。
/// </summary>
public sealed class Win32NotifyIcon : IDisposable
{
    private const uint WM_TRAY = NativeMethods.WM_USER + 1;

    // 静态委托 + 静态登记表：WndProc 必须是静态的（实例方法委托会被 GC，且 CreateWindowEx 时实例还不存在），
    // 于是回调按 hWnd 找回实例。硬要求：回调分派不许写在 lambda 里造成委托被回收。
    private static readonly WndProc _proc = OnMessage;
    private static readonly Dictionary<IntPtr, Win32NotifyIcon> ByHwnd = new();

    private readonly IntPtr _hwnd;
    private readonly IntPtr _icon;
    private NativeMethods.NOTIFYICONDATAW _nid;
    private readonly List<(int Id, string Text, Action OnPick)> _menu = new();
    private readonly object _gate = new();
    // SetTip（tooltip 泵线程 / Task 续接线程）与 Dispose（退出线程）竞的是同一个 _nid 和同一个 hWnd。
    private int _disposed;

    public Win32NotifyIcon(string icoPath, string tooltip)
    {
        var inst = NativeMethods.GetModuleHandleW(null);
        // RegisterClassW 的第一参是 `ref WNDCLASS`（派发样例直接喂了 new(...)，CS1620），所以先落成局部变量。
        var wc = new NativeMethods.WNDCLASS
        {
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
            lpszClassName = "ClashAcryTrayWnd",
            hInstance = inst,
        };
        // 注册失败不静默放过：RegisterClassW 返回 0 时 CreateWindowExW 必然报 1407「找不到窗口类」，
        // 那会把真正的死因（类没注册上）说成别人的错。真机第一次起不来就是靠这两行定位的。
        var atom = NativeMethods.RegisterClassW(ref wc);
        if (atom == 0 && Marshal.GetLastWin32Error() != 0)
            throw new InvalidOperationException($"RegisterClassW 失败（Win32Error {Marshal.GetLastWin32Error()}）");
        _hwnd = NativeMethods.CreateWindowExW(0, "ClashAcryTrayWnd", "ClashAcry", 0, 0, 0, 0, 0,
            new IntPtr(NativeMethods.HWND_MESSAGE), IntPtr.Zero, inst, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
            throw new InvalidOperationException($"消息窗口创建失败（类原子={atom}，Win32Error {Marshal.GetLastWin32Error()}）");

        lock (ByHwnd) ByHwnd[_hwnd] = this;

        _icon = NativeMethods.LoadImageW(IntPtr.Zero, icoPath, NativeMethods.IMAGE_ICON, 0, 0, NativeMethods.LR_LOADFROMFILE);
        if (_icon == IntPtr.Zero)
        {
            lock (ByHwnd) ByHwnd.Remove(_hwnd);
            NativeMethods.DestroyWindow(_hwnd);
            throw new InvalidOperationException($"图标加载失败：{icoPath}（Win32Error {Marshal.GetLastWin32Error()}）");
        }

        _nid = new NativeMethods.NOTIFYICONDATAW
        {
            cbSize = Marshal.SizeOf<NativeMethods.NOTIFYICONDATAW>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NativeMethods.NIF_MESSAGE | NativeMethods.NIF_ICON | NativeMethods.NIF_TIP | NativeMethods.NIF_SHOWTIP,
            uCallbackMessage = WM_TRAY,
            hIcon = _icon,
            szTip = tooltip,
            uVersionOrTimeout = 4,
        };
        // 裁定 R-10d③：NOTIFYICONDATAW 用默认打包即可（WinForms 的 NotifyIcon 同样默认 pack，OS 只看 cbSize
        // 做版本前向兼容）。若 NIM_ADD 返回 false，第一个要查的就是 cbSize/pack。
        if (!NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_ADD, ref _nid))
            throw new InvalidOperationException($"Shell_NotifyIcon(NIM_ADD) 失败（cbSize={_nid.cbSize}，Win32Error {Marshal.GetLastWin32Error()}）");

        _nid.uVersionOrTimeout = 4;   // NIM_SETVERSION：回调按 v4 打包（LOWORD=鼠标消息、HIWORD=uID）
        NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_SETVERSION, ref _nid);
    }

    public void MenuItem(int id, string text, Action onPick)
    {
        _menu.RemoveAll(m => m.Id == id);
        _menu.Add((id, text, onPick));
        _menu.Sort((a, b) => a.Id.CompareTo(b.Id));
    }

    public void SetTip(string tooltip)
    {
        // 图标摘掉后 hWnd 已销毁，而 tooltip 泵是后台线程（TrayHost.PumpTooltip）、异常续接走 Task 续接线程，
        // 两边都可能在 Dispose 之后再来一次 NIM_MODIFY——那既改了已释放的 _nid，也是对空句柄的调用。
        lock (_gate)
        {
            if (_disposed != 0) return;
            _nid.szTip = tooltip;
            _nid.uFlags = NativeMethods.NIF_TIP;
            NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_MODIFY, ref _nid);
        }
    }

    private static IntPtr OnMessage(IntPtr h, uint m, IntPtr w, IntPtr l)
    {
        if (m == WM_TRAY && ByHwnd.TryGetValue(h, out var self))
        {
            var (iconId, mouseMsg) = DecodeTrayParam(l.ToInt64());
            self.HandleTray(iconId, mouseMsg);
            return IntPtr.Zero;
        }
        return NativeMethods.DefWindowProcW(h, m, w, l);
    }

    /// <summary>
    /// 裁定 R-10c：<c>NIM_SETVERSION(4)</c> 之后 lParam 的打包是 <b>LOWORD=鼠标消息、HIWORD=uID</b>。
    /// 派发样例把两者反过来（(uint)l 当 uID、l&gt;&gt;32 当消息），后果是 mouseMsg 恒 0、
    /// 右键不弹菜单、双击不开界面，而这只有真机点得到——所以抽成纯函数，用两条单测把它锁住
    /// （<c>TrayCommandRoutingTests</c> 的两条 DecodeTrayParam 用例，本任务唯一的托盘回归锁）。
    /// internal：测试住在 ClashAcry.Core.Tests（配 csproj 里的 InternalsVisibleTo）。
    /// </summary>
    internal static (int IconId, int MouseMsg) DecodeTrayParam(long lParam) =>
        ((int)((lParam >> 16) & 0xFFFF), (int)(lParam & 0xFFFF));

    private void HandleTray(int iconId, int mouseMsg)
    {
        if (mouseMsg == (int)NativeMethods.WM_LBUTTONDBLCLK)
        {
            _menu.Find(m => m.Id == 1).OnPick?.Invoke();     // 双击 = 打开界面（菜单 id 1）
            return;
        }
        // WM_* 常量声明为 uint，与 int 做 `is not (A or B)` 模式匹配要显式降到 int（CS8117 家族）。
        if (mouseMsg is not ((int)NativeMethods.WM_RBUTTONUP or (int)NativeMethods.WM_CONTEXTMENU)) return;

        NativeMethods.GetCursorPos(out var p);
        var menu = NativeMethods.CreatePopupMenu();
        foreach (var (id, text, _) in _menu) NativeMethods.AppendMenuW(menu, NativeMethods.MF_STRING, (uint)id, text);
        // 未证：HWND_MESSAGE 窗口上 TrackPopupMenuEx(TPM_RETURNCMD) 是否真能响应点击（消息窗口不能
        // SetForegroundWindow，菜单可能收不到鼠标）。Task 10 的人工验证只做到「图标在、宿主稳」，
        // 右键那一下归 Task 18 冒烟；不响应就改成普通隐藏窗口（WS_POPUP 不显示）+ SetForegroundWindow
        // + GetWindowRect 定位。
        var picked = NativeMethods.TrackPopupMenuEx(menu,
            NativeMethods.TPM_RETURNCMD | NativeMethods.TPM_NONOTIFY, p.X, p.Y, _hwnd, IntPtr.Zero);
        NativeMethods.DestroyMenu(menu);                      // 弹完就毁，HMENU 不越攒越多
        if (picked > 0) _menu.Find(m => m.Id == picked).OnPick?.Invoke();
    }

    public void Dispose()
    {
        // 先摘登记表：此后到达的 WM_TRAY 回调落到 DefWindowProcW，不会再进这已经被拆了一半的实例。
        lock (ByHwnd) ByHwnd.Remove(_hwnd);
        lock (_gate)
        {
            if (_disposed != 0) return;
            _disposed = 1;
            _nid.uFlags = 0;
            NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_DELETE, ref _nid);
            NativeMethods.DestroyIcon(_icon);
            NativeMethods.DestroyWindow(_hwnd);
        }
    }
}

/// user32 的窗口过程委托类型（静态登记表按 hWnd 找实例，见 Win32NotifyIcon.OnMessage）。
internal delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
