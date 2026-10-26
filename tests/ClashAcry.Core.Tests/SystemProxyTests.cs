using Microsoft.Win32;

namespace ClashAcry.Core.Tests;

// 系统代理是进程外全局态：本集合内串行、且不与其它集合并发跑（RegistryGuard）。
[CollectionDefinition("RegistryGuard", DisableParallelization = true)]
public class RegistryGuardCollection;

[Collection("RegistryGuard")]
public class SystemProxyTests
{
    [Fact]
    public void Enable_WritesProxyEnableAndServer()
    {
        using var _ = new RegistrySnapshot();
        SystemProxy.Enable("127.0.0.1:7897", "localhost;127.*;10.*;192.168.*");

        Assert.True(SystemProxy.IsEnabled());
        Assert.Equal("127.0.0.1:7897", SystemProxy.CurrentServer());
    }

    [Fact]
    public void CleanupIfStale_DoesNotTouchForeignProxy()
    {
        using var _ = new RegistrySnapshot();
        SystemProxy.Enable("127.0.0.1:1234", "");   // 指向别的软件的代理

        SystemProxy.CleanupIfStale(7897, coreAlive: () => false);

        Assert.True(SystemProxy.IsEnabled());
        Assert.Equal("127.0.0.1:1234", SystemProxy.CurrentServer());
    }

    [Fact]
    public void CleanupIfStale_CleansOnlyWhenPointingAtUsAndCoreIsDead()
    {
        using var _ = new RegistrySnapshot();
        SystemProxy.Enable("127.0.0.1:7897", "");

        SystemProxy.CleanupIfStale(7897, coreAlive: () => false);
        Assert.False(SystemProxy.IsEnabled());

        SystemProxy.Enable("127.0.0.1:7897", "");
        SystemProxy.CleanupIfStale(7897, coreAlive: () => true);
        Assert.True(SystemProxy.IsEnabled());
    }

    [Fact]
    public void Disable_TurnsOffButKeepsForeignProxyServerString()
    {
        // 裁定 R-7c：ProxyEnable=0 时没人读 ProxyServer，而抹掉它等于删了别人（公司/其它软件）的配置。
        using var _ = new RegistrySnapshot();
        using (var k = Registry.CurrentUser.CreateSubKey(SystemProxy.InternetSettingsKey))
        {
            k.SetValue("ProxyServer", "corp.example:8080", RegistryValueKind.String);
            k.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
        }

        SystemProxy.Disable();

        Assert.False(SystemProxy.IsEnabled());
        Assert.Equal("corp.example:8080", SystemProxy.CurrentServer());
    }

    [Fact]
    public void RegistrySnapshot_RestoresAbsence_NotEmptyStrings()
    {
        // 裁定 R-7b：夹具是用户系统代理的唯一防线，而这条防线本身此前无测试。
        // 干净机器上 ProxyServer/ProxyOverride 可能不存在，样例会把它们还原成空串=永久留垃圾。
        //
        // 关键：**这条用例绝不碰真实的 Internet Settings 值**。本机实测盘上就有
        // ProxyServer=127.0.0.1:8090、ProxyOverride=localhost;127.*;…（别的软件写的），
        // 若在快照之外先删它们，夹具记录到的"原本不存在"会反过来把用户的真值删掉——
        // 夹具的保护当场变成破坏。所以用同一个夹具类打到一个 scratch 子键上，走完全相同的代码路径。
        const string scratch = SystemProxy.InternetSettingsKey + "\\ClashAcrySnapshotTest";
        Registry.CurrentUser.DeleteSubKeyTree(scratch, throwOnMissingSubKey: false);

        using (var snap = new RegistrySnapshot(scratch))               // 前置：三值都不存在
        {
            using (var k = Registry.CurrentUser.CreateSubKey(scratch))
            {
                k.SetValue("ProxyServer", "127.0.0.1:7897", RegistryValueKind.String);
                k.SetValue("ProxyOverride", "localhost;127.*", RegistryValueKind.String);
            }
            using var verify = Registry.CurrentUser.OpenSubKey(scratch);
            Assert.Equal("127.0.0.1:7897", (string?)verify!.GetValue("ProxyServer"));   // 夹具内确实写进去了
        }

        using (var k = Registry.CurrentUser.OpenSubKey(scratch))
        {
            if (k is not null)                       // 原本连子键都不存在 → 整个子键被还原掉
            {
                Assert.Null(k.GetValue("ProxyServer"));
                Assert.Null(k.GetValue("ProxyOverride"));
            }
        }
        Registry.CurrentUser.DeleteSubKeyTree(scratch, throwOnMissingSubKey: false);   // 收尾不留痕
    }
}
