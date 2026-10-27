using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace ClashAcry.UI.Views;

/// <summary>
/// 给每个页面实例盖一个「类型内递增号」，写进 UIA 的 Name——这是「切页不得有可见抖动」那条红线的可测面：
/// 实例按 Tag 复用，所以来回切页读到同一个号；一旦哪天改回每次 <c>new</c> 一个页面，号会跳，
/// FlaUI 的 ShellTests 当场变红。按类型分别计数是为了让断言写得上限明确（首个 ProxiesPage 恒为 #1）。
/// </summary>
internal static class PageIdentity
{
    private static readonly Dictionary<string, int> _seq = new();

    public static void Stamp(Page page)
    {
        var name = page.GetType().Name;
        _seq[name] = _seq.GetValueOrDefault(name) + 1;
        AutomationProperties.SetName(page, $"{name}#{_seq[name]}");
    }
}
