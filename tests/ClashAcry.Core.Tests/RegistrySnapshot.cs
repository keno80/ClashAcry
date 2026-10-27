using Microsoft.Win32;

internal sealed class RegistrySnapshot : IDisposable
{
    private static readonly string[] Names = ["ProxyEnable", "ProxyServer", "ProxyOverride"];
    private readonly string _keyPath;
    private readonly bool _keyExisted;
    private readonly bool _mayDeleteTree;
    private readonly Dictionary<string, object?> _before = new(StringComparer.Ordinal);
    private bool _changed;   // Minor②（R-7b①）：还原真的一写过东西才广播，零改动不白广播

    /// 生产用法：快照真实的 Internet Settings。
    public RegistrySnapshot() : this(ClashAcry.Core.SystemProxy.InternetSettingsKey, mayDeleteTree: false) { }

    /// 测试用法：同一个还原逻辑打到 scratch 子键上（R-7b：不许拿用户真值做破坏性前置）。
    internal RegistrySnapshot(string keyPath) : this(keyPath, mayDeleteTree: true) { }

    private RegistrySnapshot(string keyPath, bool mayDeleteTree)
    {
        _keyPath = keyPath;
        // I-1：整树删除只允许打在非生产路径上。双保险——标志位只能由 internal 构造置起，
        // 且路径等于 SystemProxy.InternetSettingsKey 时永远为 false：就算有人拿 internal 构造
        // 显式传生产路径也不给删。真实 Internet Settings 里还有 SecureProtocols、ProxyHttp1.1、
        // Connections\ 等本夹具从未记录过的值，误删即不可恢复。
        _mayDeleteTree = mayDeleteTree && keyPath != ClashAcry.Core.SystemProxy.InternetSettingsKey;
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
                if (_mayDeleteTree)
                {
                    // 连子键都是本次新造的（且只可能是 scratch 路径），整棵删掉，没有需要逐项还原的值。
                    Registry.CurrentUser.DeleteSubKeyTree(_keyPath, throwOnMissingSubKey: false);
                    return;
                }
                // I-1：生产路径快照时居然打不开 → 绝不 DeleteSubKeyTree，宁可让测试当场炸脸。
                throw new InvalidOperationException($"快照时无法打开生产注册表键 {_keyPath}，禁止整树删除，还原中止。");
            }

            // I-2：打不开就抛带名字的清晰异常，不再是 k! 的 NullReferenceException。
            using var k = Registry.CurrentUser.OpenSubKey(_keyPath, writable: true)
                ?? throw new InvalidOperationException($"还原时无法以可写方式打开注册表键 {_keyPath}。");

            List<Exception>? failures = null;
            foreach (var (name, before) in _before)
            {
                // I-2：每个值各自 try/catch、失败继续跑完其余值——Dictionary 顺序不保证，
                // 循环中途一抛就会 forfeit 排在后面的值，finally 再把半还原状态广播给所有浏览器。
                try
                {
                    var now = k.GetValue(name);
                    if (before is null)
                    {
                        if (now is not null)
                        {
                            k.DeleteValue(name, throwOnMissingValue: false);   // R-7b：还原为「不存在」，不是空串
                            _changed = true;
                        }
                    }
                    else if (!Equals(now?.ToString(), before.ToString()))
                    {
                        k.SetValue(name, before, KindOf(before));              // 只有变了才回写
                        _changed = true;
                    }
                }
                catch (Exception ex)
                {
                    (failures ??= []).Add(ex);
                }
            }

            // 所有值都有机会还原之后，测试仍然要大声失败。
            if (failures is not null)
                throw new AggregateException($"快照还原有 {failures.Count} 个值失败（其余值已尽力还原，见 InnerExceptions）。", failures);
        }
        finally
        {
            // Minor②（R-7b①）：什么都没改就不广播；scratch 键系统不读，也不广播。
            if (_changed && _keyPath == ClashAcry.Core.SystemProxy.InternetSettingsKey)
                ClashAcry.Core.SystemProxy.BroadcastChange();
        }
    }

    // 代理这三项在 Windows 里类型固定：ProxyEnable 是 REG_DWORD，另两个是 REG_SZ。
    // 不必从盘上探类型，按 CLR 值类型决定就够（多记一个 Kind 字段反而是摆设）。
    // Minor⑤：非 int 一律按 REG_SZ 处理，仅因为这三个值名类型固定才安全；若盘上出现
    // REG_MULTI_SZ（string[]），SetValue 会当场抛——本夹具专用这三项，别复用到任意键。
    private static RegistryValueKind KindOf(object value) =>
        value is int ? RegistryValueKind.DWord : RegistryValueKind.String;
}
