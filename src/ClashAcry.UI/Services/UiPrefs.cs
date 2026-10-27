using ClashAcry.Contracts;
using Microsoft.Windows.Storage;

namespace ClashAcry.UI.Services;

/// <summary>
/// 界面偏好的落点（spec §4.3 的「两棵真相树」第二棵）：材质、主题这类**只影响长什么样**的值
/// 走 unpackaged 的 LocalSettings，绝不写 <c>settings.json</c>——那份是引擎设置、由 Tray 独占原子写。
/// 取不到存储面（非 unpackaged 上下文、运行时未就绪）时全部退回默认值而不是抛：
/// 一个读不出主题的偏好不该让窗口起不来。
/// </summary>
public sealed class UiPrefs
{
    public const string DefaultBackdrop = "acrylic";      // spec §6：默认亚克力，mica/纯色可选
    public const string DefaultTheme = "system";

    private const string Publisher = "ClashAcry";      // GetForUnpackaged 的命名空间，不是包标识
    private const string AppName = "UI";

    private readonly ApplicationDataContainer? _store;

    public UiPrefs()
    {
        try { _store = ApplicationData.GetForUnpackaged(Publisher, AppName)?.LocalSettings; }
        catch (Exception) { _store = null; }
    }

    public string Backdrop
    {
        get => Read(nameof(Backdrop)) ?? DefaultBackdrop;
        set => Write(nameof(Backdrop), value);
    }

    public string Theme
    {
        get => Read(nameof(Theme)) ?? DefaultTheme;
        set => Write(nameof(Theme), value);
    }

    /// <summary>回到「全新安装」：清掉存过的键，下一次读就是各自的默认值（材质回亚克力、主题回跟随系统）。</summary>
    public void Reset()
    {
        try { _store?.Values.Clear(); }
        catch (Exception) { /* 清不掉也不该拦住窗口起来 */ }
    }

    private string? Read(string key) => _store is not null && _store.Values.TryGetValue(key, out var v) ? v as string : null;

    private void Write(string key, string value)
    {
        if (_store is not null) _store.Values[key] = value;
    }
}
