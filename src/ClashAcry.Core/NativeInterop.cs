using System.Runtime.InteropServices;

namespace ClashAcry.Core;

// 裁定 R-7a：P/Invoke 声明集中在这里，Task 10 还要往同一个文件加 user32 的声明。
internal static class NativeInterop
{
    public const int InternetOptionSettingsChanged = 39;
    public const int InternetOptionRefresh = 37;

    [DllImport("wininet.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool InternetSetOptionW(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);
}
