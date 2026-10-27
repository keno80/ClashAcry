namespace ClashAcry.Core.Proxies;

/// <summary>
/// 一行节点。两个"不变"的字段用 <c>init</c>：少一个通知就少一次重绘机会。
/// <see cref="RowId"/> 是窗口侧断言的锚点（<c>Node_{Name}</c>），必须挂在**承载整行文本的那个 TextBlock** 上——
/// WinUI 里外层 <c>ListViewItem</c>/<c>StackPanel</c> 的 UIA Name 不会聚合子元素文本，挂上去断言只会读到空串。
/// </summary>
public sealed class ProxyNodeVm : NotifyVm
{
    private string _delayText = "超时";
    private bool _selected, _isTesting, _highlight;
    private int? _lastSample;

    public ProxyNodeVm(string name, string? providerName)
    {
        Name = name; ProviderName = providerName;
    }

    /// <summary>
    /// 合并轮次的唯一状态入口。<paramref name="newestSample"/> 是健康检查样本，一旦见过就**记住**：
    /// provider 成员的样本只在 <c>GET /providers/proxies</c> 里有，下一轮 <c>/proxies</c> 依旧什么都不带，
    /// 不记住就会「上次 12 ms → 超时 → 上次 12 ms」来回跳——那正是「轮询不得有可见抖动」要禁的东西。
    /// </summary>
    public void ApplyDelay(int? liveDelay, int? newestSample)
    {
        if (liveDelay is > 0) { DelayText = $"{liveDelay} ms"; return; }
        if (newestSample is > 0) _lastSample = newestSample;
        DelayText = _lastSample is { } last ? $"上次 {last} ms" : "超时";
    }

    /// <summary>测速回来的值：正数才算测到，其余（-1 = 超时/409，已折好）如实落「超时」档，不保留旧实时值。</summary>
    public void ApplyMeasured(int delay)
    {
        DelayText = delay > 0 ? $"{delay} ms" : "超时";
    }

    public string Name { get; }
    public string? ProviderName { get; }
    public string RowId => $"Node_{Name}";

    /// <summary>行内进度标记的锚点：与行文本分开，测速中不会把 <c>Node_{Name}</c> 的读数搅浑。</summary>
    public string BadgeId => $"{RowId}_Badge";

    /// <summary>延迟三档：实时值 <c>NN ms</c> / 无实时值但有样本 <c>上次 NN ms</c> / 都没有 <c>超时</c>。
    /// 「上次」这个词是刻意的：provider 成员按 Task 1 实测拿不到活测值，冒充实时值就是谎报。</summary>
    public string DelayText
    {
        get => _delayText;
        set { if (Set(ref _delayText, value)) Raise(nameof(DisplayText)); }
    }

    public bool Selected
    {
        get => _selected;
        set { if (Set(ref _selected, value)) Raise(nameof(SelectedMark)); }
    }

    /// <summary>选中点的内容：未选中留空串，UIA 里这一列不会读出"○"这种噪声。</summary>
    public string SelectedMark => _selected ? "●" : "";

    public bool IsTesting
    {
        get => _isTesting;
        set { if (Set(ref _isTesting, value)) Raise(nameof(TestBadge)); }
    }

    /// <summary>测速中的行内标记（长耗时操作必须有进行中反馈，spec §9）。</summary>
    public string TestBadge => _isTesting ? "测速中…" : "";

    /// <summary>刚被点下的那条做一次乐观高亮（spec §7：不做乐观**状态**翻转，只做视觉指认）。</summary>
    public bool Highlight
    {
        get => _highlight;
        set { if (Set(ref _highlight, value)) Raise(nameof(HighlightOpacity)); }
    }

    public double HighlightOpacity => _highlight ? 1.0 : 0.0;

    /// <summary>整行唯一可读文本：名字与延迟同处，断言一个读取点就够。</summary>
    public string DisplayText => $"{Name} · {DelayText}";
}
