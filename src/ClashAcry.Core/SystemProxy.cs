using Microsoft.Win32;

namespace ClashAcry.Core;

public static class SystemProxy
{
    /// 测试夹具也要按这个名字读写，所以是 public（R-7b 的还原用例直接开这个键）。
    public const string InternetSettingsKey = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

    public static void Enable(string server, string bypass)
    {
        using var k = OpenWrite();
        // I-3：先写载荷、最后写开关。旧实现先翻 ProxyEnable=1 再写 ProxyServer，载荷一抛
        // 用户就被开着代理指向盘上原有的旧串（本机：别的软件的 127.0.0.1:8090）——生效行为
        // 被我们改了却没写完。现在剩下的失败形态是「server 串写了、代理仍关着」= 无害。
        var previousEnable = k.GetValue("ProxyEnable") as int? ?? 0;
        try
        {
            k.SetValue("ProxyServer", server, RegistryValueKind.String);
            if (!string.IsNullOrWhiteSpace(bypass)) k.SetValue("ProxyOverride", bypass, RegistryValueKind.String);
            k.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
        }
        catch
        {
            // 尽力回滚开关到原值；载荷先失败时开关还没被碰过，这步是同值空写。回滚再失败也不掩埋原异常。
            try { k.SetValue("ProxyEnable", previousEnable, RegistryValueKind.DWord); }
            catch (Exception) { /* 忽略：原始异常即将向上抛出，RegistrySnapshot/下次启动清理兜底 */ }
            throw;
        }
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

    // Minor①：读值也要 using——旧 OpenRead() 返回的 RegistryKey 没人 Dispose，
    // 每次 IsEnabled/CurrentServer（含 CleanupIfStale 的反复打开）都漏一个注册表句柄。
    public static bool IsEnabled()
    {
        using var k = Registry.CurrentUser.OpenSubKey(InternetSettingsKey);
        return k?.GetValue("ProxyEnable") as int? == 1;
    }

    public static string? CurrentServer()
    {
        using var k = Registry.CurrentUser.OpenSubKey(InternetSettingsKey);
        return k?.GetValue("ProxyServer") as string;
    }

    /// 三层清理中的启动校验层（spec §9）：仅当代理指向本应用端口且内核已死才清；
    /// 指向其他软件的代理绝不触碰。
    public static void CleanupIfStale(int mixedPort, Func<bool> coreAlive)
    {
        var server = CurrentServer();
        if (!IsEnabled() || server is null) return;
        var ours = $"127.0.0.1:{mixedPort}";
        // Minor③：只比对「;」首段的裸 127.0.0.1:port。分协议写法（http=127.0.0.1:7897;…）、
        // localhost:7897、[::1]:7897 都不会匹配——我们自己的残留若长成那样就清不掉。
        // 这是有意的 fail-safe 方向：宁可漏清一条自己的残留，也不误碰别人的配置，故不放宽匹配器。
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
}
