using System.Globalization;

namespace ClashAcry.Core.Proxies;

/// <summary>
/// Provider 行（spec §6「展示 <c>updatedAt</c> 与成员数」）。日期格式化与集合计数都在这里成形，
/// XAML 里不做运算——页面上只绑，断言才只有一个读取点。
/// </summary>
public sealed class ProviderRowVm : NotifyVm
{
    private DateTime? _updatedAt;
    private IReadOnlyList<string> _members;
    private bool _isRefreshing;

    public ProviderRowVm(string name, DateTime? updatedAt, IReadOnlyList<string> members)
    {
        Name = name; _updatedAt = updatedAt; _members = members;
    }

    public string Name { get; }
    public string RowId => $"Provider_{Name}";
    public string RefreshId => $"Provider_{Name}_Refresh";

    public DateTime? UpdatedAt => _updatedAt;
    public IReadOnlyList<string> Members => _members;
    public int MemberCount => _members.Count;

    /// <summary>缺 <c>updatedAt</c> 说「未知」，不许显示空串或 <c>0001-01-01</c>（后者是谎称有个时间）。
    /// 格式化走 InvariantCulture：跟着系统区域走会在泰语环境变成佛历（2569），断言与界面一起骗人。</summary>
    public string UpdatedAtText => _updatedAt?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "未知";

    /// <summary>刷新中保持旧值可见（行文本不闪空），进行中状态另有 <see cref="RefreshLabel"/>。</summary>
    public bool IsRefreshing
    {
        get => _isRefreshing;
        set { if (Set(ref _isRefreshing, value)) { Raise(nameof(RefreshLabel)); Raise(nameof(CanRefresh)); } }
    }

    /// <summary>按钮文案与可点性都由这一个状态派生：XAML 只做绑定，进行中反馈就不会和状态脱节。</summary>
    public string RefreshLabel => _isRefreshing ? "刷新中…" : "刷新";

    public bool CanRefresh => !_isRefreshing;

    public string DisplayText => $"{Name} · {UpdatedAtText} · {MemberCount} 个节点";

    /// <summary>就地更新：provider 列表本来不常变，整表重建等于给自己埋一次抖动。
    /// 成员表每轮都是新建的 List，所以**按内容比**——按引用比会让每一轮都"变了"，白通知一次。</summary>
    public void Update(DateTime? updatedAt, IReadOnlyList<string> members)
    {
        var changed = !Equals(_updatedAt, updatedAt) || !_members.SequenceEqual(members);
        _updatedAt = updatedAt;
        _members = members;
        if (!changed) return;
        Raise(nameof(UpdatedAt)); Raise(nameof(Members));
        Raise(nameof(UpdatedAtText)); Raise(nameof(MemberCount)); Raise(nameof(DisplayText));
    }
}
