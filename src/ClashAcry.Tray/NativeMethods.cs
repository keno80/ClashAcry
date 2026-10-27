using System.Runtime.InteropServices;

namespace ClashAcry.Tray;

/// <summary>
/// 托盘宿主需要的全部 user32/shell32 声明（零 WinForms、零 XAML，spec §2 §10）。
/// 集中一处的理由与 Core 的 NativeInterop 同源（裁定 R-7a）：CharSet/SetLastError 这类形状错误
/// 抄一遍就错一遍，而错的代价是「图标在、点了没反应」这种只有真机点得到、单测摸不着的现象。
/// </summary>
internal static class NativeMethods
{
    public const uint WM_USER = 0x0400,
        NIM_ADD = 0x0, NIM_MODIFY = 0x1, NIM_DELETE = 0x2, NIM_SETVERSION = 0x4,
        NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4, NIF_STATE = 0x8, NIF_SHOWTIP = 0x80,
        IMAGE_ICON = 1, LR_LOADFROMFILE = 0x10,
        WM_TRAYICON = WM_USER + 1,
        WM_LBUTTONDBLCLK = 0x0203, WM_RBUTTONUP = 0x0205, WM_CONTEXTMENU = 0x007B,
        TPM_RETURNCMD = 0x0100, TPM_NONOTIFY = 0x0080, MF_STRING = 0x0;

    /// <summary>消息窗口的「父窗口」特殊句柄：不是 HWND，是 -3。裁定 R-10d 的样例把它塞进 `const uint` 串里，
    /// 而 uint 装不下负数（CS0037 家族），所以它单独声明成 int，用的时候 `new IntPtr(NativeMethods.HWND_MESSAGE)`。</summary>
    public const int HWND_MESSAGE = -3;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct NOTIFYICONDATAW
    {
        public int cbSize; public IntPtr hWnd; public uint uID; public uint uFlags;
        public uint uCallbackMessage; public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState, dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersionOrTimeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags; public Guid guidItem; public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }

    /// <summary>
    /// 裁定 R-10d②：`message` 是 UINT（4 字节），不能像样例那样写成 IntPtr。x64 下 OS 的 MSG 布局
    /// 在 message 之后留 4 字节填充，显式写出 _pad 而不是赌 GetMessageW 把我们多给的字节清零。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd; public uint message; public uint pad;
        public IntPtr wParam; public IntPtr lParam; public uint time; public POINT pt;
    }

    /// <summary>
    /// 裁定 R-10d①：RegisterClassW 要求宽字符类名。缺 <c>CharSet.Unicode</c> 时默认按 ANSI 指针读
    /// lpszClassName，类名花掉 → CreateWindowExW 返回 0 → Shell_NotifyIcon 挂在无效 hWnd 上，
    /// 现象是「构造不抛、图标不见」。
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASS
    {
        public uint style; public IntPtr lpfnWndProc; public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATAW pnid);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr LoadImageW(IntPtr hinst, string name, uint type, int cx, int cy, uint fuLoad);

    [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr h);

    /// <summary>
    /// 实测订正（Step 6 真跑宿主抓到：托盘构造抛「消息窗口创建失败（Win32Error 1407）」= ERROR_CANNOT_FIND_WND_CLASS）：
    /// DllImport 的 <c>CharSet</c> 默认是 <b>Ansi</b>，它决定的是**字符串参数怎么编组**，与函数名后缀是 W 无关——
    /// 显式写 <c>CreateWindowExW</c> 而缺 <c>CharSet.Unicode</c> 时，类名按 ANSI 单字节流送进按 UTF-16 读的 W 版函数，
    /// 于是「类明明注册成功了却找不到」。这正是裁定 R-10d① 给 WNDCLASS 补 Unicode 的同一条错误，只是它漏了这里：
    /// R-10d① 只点名了 RegisterClassW 的类名，而派发样例的 CreateWindowExW/AppendMenuW 两行同样缺 CharSet，
    /// 编译与单测全绿、真机直接起不来（托盘连图标都没有）。AppendMenuW 少这条的后果更隐蔽：菜单能弹，
    /// 但五条中文全是乱码。
    /// </summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateWindowExW(uint ex, string cls, string title, uint style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);

    [DllImport("user32.dll")] public static extern IntPtr DefWindowProcW(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool GetMessageW(out MSG m, IntPtr h, uint first, uint last);
    [DllImport("user32.dll")] public static extern bool TranslateMessage(ref MSG m);
    [DllImport("user32.dll")] public static extern IntPtr DispatchMessageW(ref MSG m);
    [DllImport("user32.dll")] public static extern bool PostMessageW(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern IntPtr CreatePopupMenu();
    // CharSet 见 CreateWindowExW 上那条实测订正：菜单文本是中文，缺 Unicode 就是满屏乱码。
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool AppendMenuW(IntPtr menu, uint flags, uint id, string text);
    [DllImport("user32.dll")] public static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr lptpm);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] public static extern ushort RegisterClassW(ref WNDCLASS wc);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
    [DllImport("user32.dll")] public static extern bool DestroyMenu(IntPtr menu);

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

    public delegate bool CtrlHandler(uint ctrlType);

    /// <summary>
    /// 实测订正（Step 6 真跑宿主时抓到的第一条运行时故障）：简报 R-10d 的样例把
    /// <c>GetModuleHandleW</c> 放在 user32 那一组 P/Invoke 里，编译通过、单元测试全绿，
    /// 但运行时当场 <c>System.EntryPointNotFoundException: Unable to find an entry point named
    /// 'GetModuleHandleW' in DLL 'user32.dll'</c>（Win32NotifyIcon 构造第一行就炸，托盘根本挂不上）。
    /// 这个导出在 kernel32.dll（wininet 时代的老样例也常写错成 user32），故挪到 kernel32 并留这条注释。
    /// </summary>
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr GetModuleHandleW(string? name);

    /// <summary>裁定 R-10k：Program 必须把委托存进静态字段——临时委托会被 GC 掉，回调进来就是 AV。</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetConsoleCtrlHandler(CtrlHandler? handler, bool add);

    public const uint CTRL_C_EVENT = 0, CTRL_CLOSE_EVENT = 2, CTRL_LOGOFF_EVENT = 5, CTRL_SHUTDOWN_EVENT = 6;
}
