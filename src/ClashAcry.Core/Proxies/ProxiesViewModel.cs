using System.Collections.ObjectModel;
using System.Globalization;

namespace ClashAcry.Core.Proxies;

/// <summary>
/// 节点页的合并器（Task 15）。它**不吃网络**：数据从 <see cref="RefreshAsync"/> 的参数进来，
/// 五个委托只在写操作/测速时被调——这样「轮询不得有可见抖动」这条红线才能在 Core 测试集里用
/// <c>Assert.Same</c> 断言，而不是靠人眼每两秒盯一次窗口。
/// 顺序的两条来源都是确定的：<c>/proxies</c> 顶层键由 Go 的 <c>encoding/json</c> 按字母序输出
/// （实测 probe-run1/2 即如此），组内成员顺序来自 <c>all</c> 切片（引擎配置序）——两边都不是随机序，
/// 所以照抄即可；新增的组一律追加在尾部，不用 <c>Move</c>（<c>ObservableCollection.Move</c> 会让绑定层
/// 重建整段容器，那就白合并了）。
/// </summary>
public sealed class ProxiesViewModel
{
    private readonly Func<CancellationToken, Task<ProxiesResponse>> _fetch;
    private readonly Func<string, string, CancellationToken, Task> _putProxy;
    private readonly Func<string, CancellationToken, Task<int>> _measure;
    private readonly Func<string, CancellationToken, Task> _refreshProvider;

    /// <summary>最近一次的 providers 表：<c>SelectAsync</c> 重拉 <c>/proxies</c> 时要用样本续上「上次」值。</summary>
    private ProvidersResponse? _lastProviders;

    private CancellationTokenSource? _highlightTimer;

    public ProxiesViewModel(
        Func<CancellationToken, Task<ProxiesResponse>> fetch,
        Func<string, string, CancellationToken, Task> putProxy,
        Func<string, CancellationToken, Task<int>> measure,
        Func<string, CancellationToken, Task> refreshProvider,
        string delayTestUrl)
    {
        _fetch = fetch; _putProxy = putProxy; _measure = measure;
        _refreshProvider = refreshProvider; DelayTestUrl = delayTestUrl;
    }

    /// <summary>测速用的探测地址，来自 <c>handshake</c>（settings 的事实源在 Tray 那边，UI 不自己拼）。</summary>
    public string DelayTestUrl { get; }

    public ObservableCollection<ProxyGroupVm> Groups { get; } = new();
    public ObservableCollection<ProviderRowVm> ProviderRows { get; } = new();

    /// <summary>测速并发上限：真机上百个节点一起打 delay 会把控制器压成一片超时，spec §7 定 4。</summary>
    public int TestConcurrency => 4;

    /// <summary>
    /// 按名合并，绝不整表替换。组按 <c>Name</c>、成员按 <c>Name</c> 查/建，只改属性；
    /// 只有增减才 <c>Add</c>/<c>Remove</c>。<paramref name="providers"/> 传 null 表示这一轮没读 providers 表，
    /// 现有行原样留着（清空再重建 = 一次可见抖动）。
    /// </summary>
    public Task RefreshAsync(ProxiesResponse proxies, ProvidersResponse? providers)
    {
        Merge(proxies, providers);
        return Task.CompletedTask;
    }

    private void Merge(ProxiesResponse proxies, ProvidersResponse? providers)
    {
        var details = proxies.Proxies ?? new Dictionary<string, ProxyDetail>();
        if (providers is not null) _lastProviders = providers;

        var overlays = CollectOverlays(providers);
        var groupDetails = details.Values.Where(d => IsGroup(d) && !d.Hidden).ToList();

        RemoveWhere(Groups, g => groupDetails.All(d => d.Name != g.Name));
        foreach (var d in groupDetails)
        {
            var group = Groups.FirstOrDefault(g => g.Name == d.Name);
            if (group is null)
            {
                group = new ProxyGroupVm(d.Name, d.Type ?? "", d.Now);
                Groups.Add(group);
            }
            group.Type = d.Type ?? "";
            group.Now = d.Now ?? "";
            MergeMembers(group, d, details, overlays);
        }

        if (providers is not null) MergeProviderRows(providers);
    }

    /// <summary>
    /// 切换：PUT → 全量重拉 → 高亮刚点的那条。中间不做乐观翻转（spec §3 禁令 3）——
    /// 选中点只可能来自重拉后的 <c>now</c>，点了就画勾等于把「切不过去」这种情况永远藏起来。
    /// </summary>
    public async Task SelectAsync(ProxyGroupVm group, ProxyNodeVm node, CancellationToken ct)
    {
        await _putProxy(group.Name, node.Name, ct);
        var resp = await _fetch(ct);
        Merge(resp, _lastProviders);

        _highlightTimer?.Cancel();
        node.Highlight = true;
        var token = (_highlightTimer = new CancellationTokenSource()).Token;
        _ = ClearHighlightAsync(node, token);
    }

    public Task TestGroupAsync(ProxyGroupVm group, CancellationToken ct) =>
        TestNodesAsync(group.MeasurableNodes, ct);

    public Task TestAllAsync(CancellationToken ct) =>
        TestNodesAsync(Groups.SelectMany(g => g.MeasurableNodes).ToList(), ct);

    /// <summary>
    /// Provider 手动刷新：只发 PUT 并立进行中标记，重拉由调用方的轮询做——
    /// VM 没有读 providers 的委托，自己拉不到新 <c>updatedAt</c>，本地猜时间就是造第二个事实源。
    /// </summary>
    public async Task RefreshProviderAsync(string providerName, CancellationToken ct)
    {
        var row = ProviderRows.FirstOrDefault(r => r.Name == providerName);
        try
        {
            if (row is not null) row.IsRefreshing = true;
            await _refreshProvider(providerName, ct);
        }
        finally
        {
            if (row is not null) row.IsRefreshing = false;
        }
    }

    // ---- 合并细节 ---------------------------------------------------------------

    /// <summary>
    /// 成员行的合并。<b>Task 1 实测两处必须照办</b>：
    /// ① provider 成员**不出现在** <c>/proxies</c> 顶层表，只出现在组的 <c>all</c> 与
    ///    <c>/providers/proxies</c> 的成员表里——所以顶层查不到条目不等于这个成员不存在，必须照常建行；
    /// ② 非 provider 条目的 <c>provider-name</c> 是**空串**（不是缺键，probe 里是 <c>"provider-name":""</c>），
    ///    不折成 null 就会被当成订阅成员，全组测速一条都不测。
    /// </summary>
    private static void MergeMembers(ProxyGroupVm group, ProxyDetail detail,
        Dictionary<string, ProxyDetail> details, Dictionary<string, ProxyDetail> overlays)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var wanted = new List<(string Name, ProxyDetail? Top, ProxyDetail? Overlay)>();
        foreach (var name in detail.All ?? new List<string>())
        {
            if (!seen.Add(name)) continue;                                   // all 里的重名只留一条：两行同 id 会让断言与点击都认错
            var top = details.TryGetValue(name, out var t) ? t : null;
            if (top is { Hidden: true } || IsGroup(top)) continue;           // mihomo 自标的内部隐藏项与嵌套组都不进树
            var overlay = overlays.TryGetValue(name, out var o) ? o : null;
            wanted.Add((name, top, overlay));
        }

        RemoveWhere(group.Nodes, vm => wanted.All(w => w.Name != vm.Name));

        foreach (var (name, top, overlay) in wanted)
        {
            var vm = group.Nodes.FirstOrDefault(n => n.Name == name);
            if (vm is null)
            {
                vm = new ProxyNodeVm(name, NormalizeProvider(top?.ProviderName) ?? NormalizeProvider(overlay?.ProviderName));
                group.Nodes.Add(vm);
            }
            // 顶层条目一般不带 delay（订阅成员的实时值与样本只在 providers 表里），两侧取信息量大的那个。
            vm.ApplyDelay(FirstPositive(top?.Delay, overlay?.Delay),
                NewestSample(overlay?.History) ?? NewestSample(top?.History));
            vm.Selected = vm.Name == detail.Now;
        }
    }

    /// <summary>mihomo 用空串表示"不是订阅成员"（实测 <c>"provider-name":""</c>）；界面上要的"不是成员"是 null。</summary>
    private static string? NormalizeProvider(string? name) => string.IsNullOrWhiteSpace(name) ? null : name;

    private static int? FirstPositive(int? a, int? b) => a is > 0 ? a : b is > 0 ? b : null;

    /// <summary>
    /// providers 表按成员名摊平。<b>不过滤 vehicleType</b>：伪 provider（Compatible/default）里列的就是组内节点，
    /// 按名覆盖是无操作；过滤反而会让真成员的样本被漏掉。过滤只作用于 <see cref="ProviderRows"/>。
    /// 同名成员在多张表里都出现时（真 provider + 各组的 Compatible 伪 provider 都会列它），
    /// 留信息量大的那条：有 <c>provider-name</c> 的优先，其次是有健康样本的。
    /// </summary>
    private static Dictionary<string, ProxyDetail> CollectOverlays(ProvidersResponse? providers)
    {
        var map = new Dictionary<string, ProxyDetail>(StringComparer.Ordinal);
        if (providers?.Providers is null) return map;
        foreach (var p in providers.Providers.Values)
            foreach (var member in p.Proxies ?? new List<ProxyDetail>())
                if (!map.TryGetValue(member.Name, out var old) || CarriesMore(old, member)) map[member.Name] = member;
        return map;
    }

    private static bool CarriesMore(ProxyDetail current, ProxyDetail candidate) =>
        NormalizeProvider(candidate.ProviderName) is not null && NormalizeProvider(current.ProviderName) is null
        || (NewestSample(candidate.History) is not null && NewestSample(current.History) is null);

    private void MergeProviderRows(ProvidersResponse providers)
    {
        // Task 1 实测：表里混着每个策略组自动生成的 Compatible 伪 provider 和 default，
        // 真订阅只有 File/Http 两种 vehicleType；不过滤就会长出一堆能对它们发 PUT 的假行。
        var real = providers.Providers.Values
            .Where(p => string.Equals(p.VehicleType, "File", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(p.VehicleType, "Http", StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.Name, StringComparer.Ordinal)      // 按名排序：字典序稳定，轮询不跳行
            .ToList();

        RemoveWhere(ProviderRows, r => real.All(p => p.Name != r.Name));
        foreach (var p in real)
        {
            var members = (p.Proxies ?? new List<ProxyDetail>()).Select(m => m.Name).ToList();
            var row = ProviderRows.FirstOrDefault(r => r.Name == p.Name);
            if (row is null) ProviderRows.Add(new ProviderRowVm(p.Name, p.UpdatedAt, members));
            else row.Update(p.UpdatedAt, members);
        }
    }

    /// <summary>
    /// 取最新一次健康样本。probe 只抓到长度 ≤1 的 history，「哪端是最新」没有实测依据，
    /// 所以这里不赌列表位置、只比 <c>time</c>：按时间取最大，顺序怎么变都是同一条。
    /// 全部时间戳都解析不了才退回末位（mihomo 是追加序，末位即最新）。
    /// </summary>
    private static int? NewestSample(List<DelaySample>? history)
    {
        if (history is not { Count: > 0 }) return null;
        int? lastPositive = null;
        int? byTime = null;
        DateTimeOffset best = default;
        foreach (var s in history)
        {
            if (s.Effective is not > 0) continue;               // delay=0 是「这次没测通」，不是 0 ms
            lastPositive = s.Effective;
            if (TryTime(s.Time, out var t) && (byTime is null || t > best)) { byTime = s.Effective; best = t; }
        }
        return byTime ?? lastPositive;
    }

    private static bool TryTime(string? time, out DateTimeOffset parsed) =>
        DateTimeOffset.TryParse(time, CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed);

    /// <summary>倒序摘除：<c>ObservableCollection</c> 没有 <c>RemoveAll</c>，正序边扫边删会把索引踩掉。</summary>
    private static void RemoveWhere<T>(ObservableCollection<T> items, Func<T, bool> drop)
    {
        for (var i = items.Count - 1; i >= 0; i--)
            if (drop(items[i])) items.RemoveAt(i);
    }

    private async Task TestNodesAsync(IReadOnlyList<ProxyNodeVm> nodes, CancellationToken ct)
    {
        using var gate = new SemaphoreSlim(TestConcurrency);
        // 许可一到手就开测、测完立刻还：IsTesting 必须覆盖「在飞」的整段，否则进度标记比请求先落地。
        var running = nodes.Select(async vm =>
        {
            await gate.WaitAsync(ct);
            try
            {
                vm.IsTesting = true;
                vm.ApplyMeasured(await _measure(vm.Name, ct));
            }
            finally
            {
                vm.IsTesting = false;
                gate.Release();
            }
        });
        await Task.WhenAll(running);
    }

    private static async Task ClearHighlightAsync(ProxyNodeVm node, CancellationToken ct)
    {
        try { await Task.Delay(1200, ct); node.Highlight = false; }
        catch (OperationCanceledException) { /* 已被下一次选择接管 */ }
    }

    private static bool IsGroup(ProxyDetail? d) => d?.All is { Count: > 0 };
}
