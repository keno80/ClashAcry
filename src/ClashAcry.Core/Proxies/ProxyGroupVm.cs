using System.Collections.ObjectModel;

namespace ClashAcry.Core.Proxies;

/// <summary>
/// 一个策略组（select / url-test / relay / fallback）。成员顺序照抄 mihomo 的 <c>all</c>——
/// 引擎顺序就是事实源，VM 重排会让行序每轮跳一次（spec §7）。
/// </summary>
public sealed class ProxyGroupVm : NotifyVm
{
    public ProxyGroupVm(string name, string type, string? now)
    {
        Name = name; _type = type; _now = now ?? "";
    }

    public string Name { get; }
    public string GroupId => $"Group_{Name}";

    /// <summary>折叠箭头的 id 沿用计划的 <c>Node_{组名}_Collapse</c> 写法；行本体不设点击，
    /// 点整行会撞上「选中节点」这个主操作。</summary>
    public string CollapseId => $"Node_{Name}_Collapse";

    private string _type, _now;
    public string Type { get => _type; set => Set(ref _type, value); }

    /// <summary>当前选中项，来源永远是 mihomo 的 <c>now</c>（本地不猜、不乐观翻转）。</summary>
    public string Now
    {
        get => _now;
        set { if (Set(ref _now, value)) Raise(nameof(HeaderText)); }
    }

    /// <summary>组行文本：组名 + 当前选择，一个读取点。</summary>
    public string HeaderText => _now.Length == 0 ? Name : $"{Name} · {_now}";

    public ObservableCollection<ProxyNodeVm> Nodes { get; } = new();

    /// <summary>组内「测速」按钮的锚点。</summary>
    public string TestId => $"Group_{Name}_Test";

    public bool Expanded
    {
        get => _expanded;
        set { if (Set(ref _expanded, value)) { Raise(nameof(CollapseGlyph)); Raise(nameof(MembersVisible)); } }
    }
    private bool _expanded = true;      // 默认展开：折叠的容器不在 UIA 树里，断言与取样都取不到

    /// <summary>Segoe MDL2 的向下 / 向右尖角（E70D / E76E），给 <c>FontIcon.Glyph</c> 直接绑。</summary>
    public string CollapseGlyph => _expanded ? "\uE70D" : "\uE76E";

    public bool MembersVisible => _expanded;

    /// <summary>组内可测速的成员：provider 成员一律排除（实测 <c>/proxies/{成员}/delay</c> 404，spec §4.2）。</summary>
    public IReadOnlyList<ProxyNodeVm> MeasurableNodes => Nodes.Where(n => n.ProviderName is null).ToList();
}
