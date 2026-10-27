using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace ClashAcry.Core;

public static class SystemProxy
{
    /// 测试夹具也要按这个名字读写，所以是 public（R-7b 的还原用例直接开这个键）。
    public const string InternetSettingsKey = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

    /// 我们自己写进去的绕过表。唯一一份：冷启动清理与 sysproxy.set 共用同一个串
    /// （R-20b：还原要判"当前值还是不是我们写的那份"，两处串不一样就判不准）。
    public const string DefaultBypass = "localhost,127.0.0.1,<local>";

    /// 接手前的两键原值。<b>字段为 null 的含义是"原本不存在"</b>，还原时按不存在处理（删除），
    /// 不许写成空串——R-7b 在夹具那侧立的规矩，这里同一口径。
    /// 键名逐字段显式标注（与 `Settings`/`Frame` 同一裁定）：这份快照要落进 settings.json，靠命名策略推断等于埋一颗改名雷。
    public record Backup([property: JsonPropertyName("server")] string? Server,
                         [property: JsonPropertyName("bypass")] string? Bypass);

    /// 读回我们接手前（或此刻）的两键原值。
    public static Backup Capture()
    {
        using var k = Registry.CurrentUser.OpenSubKey(InternetSettingsKey);
        return new Backup(k?.GetValue("ProxyServer") as string, k?.GetValue("ProxyOverride") as string);
    }

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
    /// 原值的归还由 <see cref="Restore"/> 负责（R-20b）：开关永远是我们自己的动作，载荷只在还是我们那份时才还。
    public static void Disable()
    {
        using var k = OpenWrite();
        k.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
        BroadcastChange();
    }

    /// <summary>
    /// 裁定 R-20b：把 <see cref="Capture"/> 到的原值写回去，**但只在当前值仍等于我们那一份时**。
    /// 判据取 <c>ProxyServer</c> 而不是 <c>ProxyEnable</c>——别人可以在我们开着的时候改载荷而不动开关。
    /// 第三方改过地址 ⇒ 我们那份快照已过期，写回去等于反向抹掉别人，与 R-7c 同罪，所以直接返回假、什么都不写。
    /// 返回真 = 已还原，调用方据此清掉快照；返回假 = 快照继续留着，别自己猜。
    /// </summary>
    public static bool Restore(Backup backup, string ourServer, string ourBypass)
    {
        var now = Capture();
        if (!string.Equals(now.Server, ourServer, StringComparison.OrdinalIgnoreCase)) return false;
        // 绕过表只在「我们真的写过它」时参与比对：Enable 对空白 bypass 是跳过写的（见 I-3 那段）。
        if (!string.IsNullOrWhiteSpace(ourBypass) &&
            !string.Equals(now.Bypass, ourBypass, StringComparison.OrdinalIgnoreCase)) return false;

        using (var k = OpenWrite())
        {
            WriteOrDelete(k, "ProxyServer", backup.Server);
            WriteOrDelete(k, "ProxyOverride", backup.Bypass);
        }
        BroadcastChange();
        return true;
    }

    private static void WriteOrDelete(RegistryKey k, string name, string? value)
    {
        if (string.IsNullOrEmpty(value)) k.DeleteValue(name, throwOnMissingValue: false);
        else k.SetValue(name, value, RegistryValueKind.String);
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
    /// 返回值 = **是否清掉了我们自己那条残留**（R-20b）：调用方只在真时才去动原值快照，
    /// 免得"没清我们"却把别人的 ProxyServer 当成我们的还原了一遍。
    public static bool CleanupIfStale(int mixedPort, Func<bool> coreAlive)
    {
        var server = CurrentServer();
        if (!IsEnabled() || server is null) return false;
        var ours = $"127.0.0.1:{mixedPort}";
        // Minor③：只比对「;」首段的裸 127.0.0.1:port。分协议写法（http=127.0.0.1:7897;…）、
        // localhost:7897、[::1]:7897 都不会匹配——我们自己的残留若长成那样就清不掉。
        // 这是有意的 fail-safe 方向：宁可漏清一条自己的残留，也不误碰别人的配置，故不放宽匹配器。
        var normalized = server.Split(';')[0].Trim();
        if (!normalized.Equals(ours, StringComparison.OrdinalIgnoreCase)) return false;
        if (coreAlive()) return false;
        Disable();
        return true;
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
