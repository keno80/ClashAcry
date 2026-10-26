using Microsoft.Win32;

internal sealed class RegistrySnapshot : IDisposable
{
    private static readonly string[] Names = ["ProxyEnable", "ProxyServer", "ProxyOverride"];
    private readonly string _keyPath;
    private readonly bool _keyExisted;
    private readonly Dictionary<string, object?> _before = new(StringComparer.Ordinal);

    /// 生产用法：快照真实的 Internet Settings。
    public RegistrySnapshot() : this(ClashAcry.Core.SystemProxy.InternetSettingsKey) { }

    /// 测试用法：同一个还原逻辑打到 scratch 子键上（R-7b：不许拿用户真值做破坏性前置）。
    internal RegistrySnapshot(string keyPath)
    {
        _keyPath = keyPath;
        _keyExisted = Registry.CurrentUser.OpenSubKey(keyPath) is not null;
        using var k = Registry.CurrentUser.OpenSubKey(keyPath);
        foreach (var name in Names) _before[name] = k?.GetValue(name);   // null = 原本不存在
    }

    public void Dispose()
    {
        try
        {
            if (!_keyExisted)
            {
                Registry.CurrentUser.DeleteSubKeyTree(_keyPath, throwOnMissingSubKey: false);
                return;                                   // 子键都是新造的，里面没有需要逐个还原的值
            }
            using var k = Registry.CurrentUser.OpenSubKey(_keyPath, writable: true);
            foreach (var (name, before) in _before)
            {
                var now = k!.GetValue(name);
                if (before is null) k.DeleteValue(name, throwOnMissingValue: false);   // 还原为「不存在」
                else if (!Equals(now?.ToString(), before.ToString()))
                    k.SetValue(name, before, KindOf(before));                          // 只有变了才回写
            }
        }
        finally
        {
            if (_keyPath == ClashAcry.Core.SystemProxy.InternetSettingsKey)
                ClashAcry.Core.SystemProxy.BroadcastChange();   // scratch 键系统不读，别白广播
        }
    }

    // 代理这三项在 Windows 里类型固定：ProxyEnable 是 REG_DWORD，另两个是 REG_SZ。
    // 不必从盘上探类型，按 CLR 值类型决定就够（多记一个 Kind 字段反而是摆设）。
    private static RegistryValueKind KindOf(object value) =>
        value is int ? RegistryValueKind.DWord : RegistryValueKind.String;
}
