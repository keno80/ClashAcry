using Microsoft.Win32;

namespace ClashAcry.Core;

public static class SystemProxy
{
    /// 测试夹具也要按这个名字读写，所以是 public（R-7b 的还原用例直接开这个键）。
    public const string InternetSettingsKey = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

    public static void Enable(string server, string bypass)
    {
        using var k = OpenWrite();
        k.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
        k.SetValue("ProxyServer", server, RegistryValueKind.String);
        if (!string.IsNullOrWhiteSpace(bypass)) k.SetValue("ProxyOverride", bypass, RegistryValueKind.String);
        BroadcastChange();
    }

    /// 裁定 R-7c：只关开关。ProxyEnable=0 时没人读 ProxyServer，而抹掉它可能抹的是别人（公司代理/其它
    /// 软件）写的地址——本任务的标题就是「只清自己的」。ProxyOverride 同理不碰。
    public static void Disable()
    {
        using var k = OpenWrite();
        k.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
        BroadcastChange();
    }

    public static bool IsEnabled() => OpenRead()?.GetValue("ProxyEnable") as int? == 1;
    public static string? CurrentServer() => OpenRead()?.GetValue("ProxyServer") as string;

    /// 三层清理中的启动校验层（spec §9）：仅当代理指向本应用端口且内核已死才清；
    /// 指向其他软件的代理绝不触碰。
    public static void CleanupIfStale(int mixedPort, Func<bool> coreAlive)
    {
        var server = CurrentServer();
        if (!IsEnabled() || server is null) return;
        var ours = $"127.0.0.1:{mixedPort}";
        var normalized = server.Split(';')[0].Trim();
        if (!normalized.Equals(ours, StringComparison.OrdinalIgnoreCase)) return;
        if (coreAlive()) return;
        Disable();
    }

    public static void BroadcastChange()
    {
        NativeInterop.InternetSetOptionW(IntPtr.Zero, NativeInterop.InternetOptionSettingsChanged, IntPtr.Zero, 0);
        NativeInterop.InternetSetOptionW(IntPtr.Zero, NativeInterop.InternetOptionRefresh, IntPtr.Zero, 0);
    }

    private static RegistryKey OpenWrite() =>
        Registry.CurrentUser.CreateSubKey(InternetSettingsKey, RegistryKeyPermissionCheck.ReadWriteSubTree)
        ?? throw new InvalidOperationException("无法打开 Internet Settings 键");
    private static RegistryKey? OpenRead() => Registry.CurrentUser.OpenSubKey(InternetSettingsKey);
}
