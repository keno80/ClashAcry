using Microsoft.Win32;

namespace ClashAcry.Core.Tests;

// 系统代理是进程外全局态，必须独占跑。注意代价（xUnit v2 语义）：集合定义上的
// DisableParallelization = true 不是只串行本集合，而是让整个 ClashAcry.Core.Tests 程序集
// 都退化为串行执行——WireMock、临时目录等所有测试一起排队。此代价已被接受：全局注册表态
// 只要有一条用例碰它，任何并发（哪怕别的集合）都可能互相踩。
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

        try
        {
            using (var snap = new RegistrySnapshot(scratch))               // 前置：连子键都不存在
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
                // 断言必须无条件：子键是夹具期间新造的（_keyExisted == false），Dispose 就必须
                // 把整棵 scratch 删掉。旧的 `if (k is not null)` 容许两种分支，等于这条什么都没锁。
                Assert.Null(k);
            }
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(scratch, throwOnMissingSubKey: false);   // 收尾不留痕
        }
    }

    [Fact]
    public void RegistrySnapshot_ExistingKeyWithAbsentValues_DeletesValues_NotEmptyStrings()
    {
        // I-4／R-7b 回归锁：覆盖「子键已存在、但被快照的值原本不存在」这条姊妹路径。
        // 上一条用例走的是 _keyExisted == false 分支、Dispose 在逐项还原循环之前就 return 了，
        // 而本机三个真值都存在——于是 `before is null → DeleteValue(name)` 这行在整个套件里
        // 没有任何测试执行过。若把它退化成 SetValue(name, "")（正是 R-7b 要防的 bug），
        // 旧套件全绿、垃圾空串永久留在用户注册表里。这条用例把它锁死：
        // 还原后 ProxyServer/ProxyOverride 必须读回 null（被删除），而不是空串；
        // 夹具没记录过的哨兵值必须原样幸存；子键是预先存在的，Dispose 绝不许删整棵树（I-1）。
        const string scratch = SystemProxy.InternetSettingsKey + "\\ClashAcrySnapshotTest";
        Registry.CurrentUser.DeleteSubKeyTree(scratch, throwOnMissingSubKey: false);

        try
        {
            using (var k = Registry.CurrentUser.CreateSubKey(scratch))
                k.SetValue("Sentinel", "keepme", RegistryValueKind.String);   // 快照之前就存在的值

            using (var snap = new RegistrySnapshot(scratch))              // _keyExisted == true，三值皆 null
            {
                using (var k = Registry.CurrentUser.CreateSubKey(scratch))
                {
                    k.SetValue("ProxyServer", "127.0.0.1:7897", RegistryValueKind.String);
                    k.SetValue("ProxyOverride", "localhost;127.*", RegistryValueKind.String);
                }
            }

            using var after = Registry.CurrentUser.OpenSubKey(scratch);
            Assert.NotNull(after);                                            // 子键原本存在 → 不许被整树删掉
            Assert.Null(after!.GetValue("ProxyServer"));                      // 不存在 → 还原为不存在（非空串）
            Assert.Null(after.GetValue("ProxyOverride"));
            Assert.Equal("keepme", (string?)after.GetValue("Sentinel"));      // 夹具只管那三个值，哨兵不许碰
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(scratch, throwOnMissingSubKey: false);   // 收尾不留痕
        }
    }
}
