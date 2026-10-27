using ClashAcry.Core;
using ClashAcry.Core.Proxies;

namespace ClashAcry.Core.Tests;

/// <summary>
/// 节点页的合并逻辑（Task 15）。放 Core 测有两个理由：`ObservableCollection` 属 `System.ObjectModel`、
/// 不需要 UI 工程；而**合并逻辑是「轮询不得有可见抖动」这条红线的唯一可实现处**——放在页面里就只能靠人眼
/// 盯窗口，那是验收不是测试。这里的 `Assert.Same` 就是那条红线的可执行形式：同实例复用 = 容器不重建。
/// </summary>
public class ProxiesViewModelTests
{
    private static ProxyDetail Group(string name, string now, params string[] all) => new()
    { Name = name, Type = "select", Now = now, All = all.ToList(), Hidden = false };

    private static ProxyDetail Node(string name, string? provider = null, int? liveDelay = null,
                                    params DelaySample[] history) => new()
    { Name = name, Type = "ss", ProviderName = provider, Delay = liveDelay, History = history.ToList() };

    private static ProxiesResponse Resp(params ProxyDetail[] details) =>
        new(details.ToDictionary(d => d.Name, d => d));

    // 五个委托全靠注入：fetch 默认返回空表，measure 默认 -1（"拿不到实时值"），记录用的 List 由用例自己传进来
    private static ProxiesViewModel Vm(
        Func<string, CancellationToken, Task<int>>? measure = null,
        List<(string Group, string Node)>? puts = null,
        List<string>? providerRefreshes = null,
        Func<CancellationToken, Task<ProxiesResponse>>? fetch = null) => new(
            fetch ?? (ct => Task.FromResult(Resp())),
            (g, n, ct) => { puts?.Add((g, n)); return Task.CompletedTask; },
            measure ?? ((n, ct) => Task.FromResult(-1)),
            (p, ct) => { providerRefreshes?.Add(p); return Task.CompletedTask; },
            "http://www.gstatic.com/generate_204");

    [Fact]
    public async Task Refresh_MatchesExistingNodeVmsByName_AndKeepsVisualTreeItems()
    {
        var vm = Vm();
        await vm.RefreshAsync(Resp(Group("G", "n1", "n1", "n2"), Node("n1"), Node("n2")), null);
        var first = vm.Groups[0].Nodes[0];

        await vm.RefreshAsync(Resp(Group("G", "n1", "n1", "n2"), Node("n1", liveDelay: 99), Node("n2")), null);

        Assert.Same(first, vm.Groups[0].Nodes[0]);                 // 同实例复用：容器不重建，ItemContainer 不重挂
        Assert.Equal("99 ms", first.DelayText);                    // 数值就地更新
        Assert.Equal(2, vm.Groups[0].Nodes.Count);
    }

    [Fact]
    public async Task Refresh_AddsAndRemovesOnlyChangedMembers()
    {
        var vm = Vm();
        await vm.RefreshAsync(Resp(Group("G", "n1", "n1"), Node("n1")), null);
        var survivor = vm.Groups[0].Nodes[0];

        await vm.RefreshAsync(Resp(Group("G", "n1", "n1", "n3"), Node("n1"), Node("n3")), null);   // 新增 n3
        Assert.Same(survivor, vm.Groups[0].Nodes[0]);
        Assert.Equal(new[] { "n1", "n3" }, vm.Groups[0].Nodes.Select(n => n.Name));

        await vm.RefreshAsync(Resp(Group("G", "n3", "n3"), Node("n3")), null);                     // 移除 n1
        Assert.Equal(new[] { "n3" }, vm.Groups[0].Nodes.Select(n => n.Name));
        Assert.Equal("n3", vm.Groups[0].Now);                              // 选中项来源永远是 mihomo 的 now（spec §7）
    }

    [Fact]
    public async Task MemberWithoutLiveDelay_ShowsLastSampleMarkedShangCi()
    {
        // provider 成员按 Task 1 实测拿不到活测值：history 最新样本 + 「上次」标记，不冒充实时值（spec §12）
        var vm = Vm();
        await vm.RefreshAsync(Resp(Group("G", "n1", "n1"), Node("n1", provider: "sub-a")),
            new ProvidersResponse(new Dictionary<string, ProviderInfo>
            {
                ["sub-a"] = new() { Name = "sub-a", VehicleType = "Http", Proxies = new() { Node("n1", "sub-a", liveDelay: null, history: new[] { new DelaySample("t", 12, null) }) } }
            }));

        Assert.Equal("上次 12 ms", vm.Groups[0].Nodes[0].DelayText);
        Assert.Equal("sub-a", vm.Groups[0].Nodes[0].ProviderName);
    }

    [Fact]
    public async Task Providers_AreProjectedAsRowsWithNameTimeAndCount()
    {
        // spec §6「Provider：手动刷新，展示 updatedAt 与成员数」——这三样在 VM 里成形，页面上只绑不做运算
        // Task 1 实测：`GET /providers/proxies` 的表里混有每个策略组自动生成的 `vehicleType:"Compatible"`
        // 伪 provider 与一个 `default`。VM 必须按 vehicleType 过滤（只留 File / Http），否则页面上是一堆假行。
        var vm = Vm();
        await vm.RefreshAsync(Resp(Group("G", "n1", "n1"), Node("n1", provider: "sub-a")),
            new ProvidersResponse(new Dictionary<string, ProviderInfo>
            {
                ["sub-b"] = new() { Name = "sub-b", VehicleType = "File", UpdatedAt = new DateTime(2026, 10, 26, 8, 30, 0),
                                    Proxies = new() { Node("n2", "sub-b"), Node("n3", "sub-b") } },
                ["sub-a"] = new() { Name = "sub-a", VehicleType = "Http", UpdatedAt = new DateTime(2026, 10, 25, 1, 2, 0),
                                    Proxies = new() { Node("n1", "sub-a") } },
                ["G"] = new() { Name = "G", VehicleType = "Compatible",
                                Proxies = new() { Node("n1", null) } },           // 组自动生成的伪 provider：不得成行
                ["default"] = new() { Name = "default", VehicleType = "Compatible", Proxies = new() { } },
            }));

        Assert.Equal(new[] { "sub-a", "sub-b" }, vm.ProviderRows.Select(r => r.Name));   // 按名排序：轮询不得让行序跳动
        Assert.Equal(1, vm.ProviderRows[0].MemberCount);
        Assert.Equal(2, vm.ProviderRows[1].MemberCount);
        Assert.Contains("2026-10-26", vm.ProviderRows[1].UpdatedAtText);                  // 由 VM 格式化
        Assert.Equal("未知", new ProviderRowVm("x", null, Array.Empty<string>()).UpdatedAtText);              // updatedAt 缺失不许显示空或 0001-01-01
    }

    [Fact]
    public async Task MemberWithNeitherLiveNorHistory_ShowsTimeout()
    {
        var vm = Vm();
        await vm.RefreshAsync(Resp(Group("G", "n1", "n1"), Node("n1", provider: "sub-a")), null);
        Assert.Equal("超时", vm.Groups[0].Nodes[0].DelayText);
    }

    [Fact]
    public async Task Select_PutsThenRefetches_AndHighlightsOnce()
    {
        var puts = new List<(string Group, string Node)>();
        var fetches = 0;
        var after = Resp(Group("G", "n2", "n1", "n2"), Node("n1"), Node("n2"));      // 服务端已切到 n2
        var vm = Vm(puts: puts, fetch: ct => { fetches++; return Task.FromResult(after); });

        await vm.RefreshAsync(Resp(Group("G", "n1", "n1", "n2"), Node("n1"), Node("n2")), null);
        fetches = 0;
        await vm.SelectAsync(vm.Groups[0], vm.Groups[0].Nodes[1], default);           // 选 n2

        Assert.Equal(("G", "n2"), puts.Single());
        Assert.Equal(1, fetches);                                                     // PUT 之后全量重拉，本地不猜
        Assert.True(vm.Groups[0].Nodes[1].Highlight);                                 // 刚点的那条高亮，结构不变
        Assert.Equal("n2", vm.Groups[0].Nodes[1].Name);
        Assert.Equal("n2", vm.Groups[0].Now);                                         // 选中态来自重拉结果
    }

    [Fact]
    public async Task TestAll_NeverExceedsFourInFlight()
    {
        int live = 0, peak = 0;
        var gate = new object();
        var names = Enumerable.Range(0, 12).Select(i => $"n{i}").ToArray();
        var data = Resp(new[] { Group("G", "n0", names) }.Concat(names.Select(n => Node(n))).ToArray());
        var vm = Vm(fetch: ct => Task.FromResult(data), measure: async (n, ct) =>
        {
            lock (gate) peak = Math.Max(peak, ++live);
            await Task.Delay(40, ct);
            lock (gate) live--;
            return 20;
        });

        await vm.RefreshAsync(data, null);
        await vm.TestAllAsync(default);

        Assert.Equal(4, peak);                                                   // 并发上限是硬约束，不是"大概"
        Assert.Equal(12, vm.Groups[0].Nodes.Count(n => n.DelayText == "20 ms"));
        Assert.All(vm.Groups[0].Nodes, n => Assert.False(n.IsTesting));          // 全部收尾，不留"测速中"僵尸态
    }

    [Fact]
    public async Task ProviderRefresh_GoesThroughRegisteredEndpoint()
    {
        var refreshed = new List<string>();
        var vm = Vm(providerRefreshes: refreshed);
        await vm.RefreshAsync(Resp(Group("G", "n1", "n1"), Node("n1", provider: "sub-a")), null);

        await vm.RefreshProviderAsync("sub-a", default);

        Assert.Equal(new[] { "sub-a" }, refreshed);                              // 手动刷新走 PUT /providers/proxies/{name}
    }

    // ---- 下面两条锁的是「provider 成员绝不走 /proxies/{name}/delay」与 hidden 过滤：
    //      前者是 Task 1 的 404 实测（spec §4.2 既定实现要求），后者是节点树不被 200 行假项淹没的前提。

    [Fact]
    public async Task TestAll_NeverMeasuresProviderMembers()
    {
        // 实测 v1.19.33：对 provider 成员名调 /proxies/{name}/delay 一律 404，且 `MihomoClient.GetDelayAsync`
        // 有意**不吞 404**（吞了就永远看不出通路用错）。所以这里断言的是「一次都没调用」，
        // 而不是「调用了但被折成超时」——后者在生产里是一串抛穿的 404。
        var measured = new List<string>();
        var vm = Vm(measure: (n, ct) => { measured.Add(n); return Task.FromResult(10); });
        var resp = Resp(
            Group("G", "own", "own", "member"),
            Node("own"),
            Node("member", provider: "sub-a"));
        await vm.RefreshAsync(resp, new ProvidersResponse(new Dictionary<string, ProviderInfo>
        {
            ["sub-a"] = new() { Name = "sub-a", VehicleType = "Http", Proxies = new() { Node("member", "sub-a", history: new[] { new DelaySample("t", 33, null) }) } }
        }));

        await vm.TestAllAsync(default);

        Assert.Equal(new[] { "own" }, measured);                                  // 只测非 provider 成员
        Assert.Equal("上次 33 ms", vm.Groups[0].Nodes[1].DelayText);               // 成员延迟只来自 providers 的样本
    }

    [Fact]
    public async Task Refresh_DropsHiddenMembersAndHiddenGroups()
    {
        // mihomo 的 `hidden: true` 是引擎自己标的内部项（缓存组、`default` 之类）。v1 一律不进树：
        // 用户看得见却切不动的东西，比看不见更糟。`ProxyDetail` 是 class（System.Text.Json 反序列化需要），
        // 所以这里直接改属性，不能用 `with`。
        var hiddenNode = Node("n2"); hiddenNode.Hidden = true;
        var hiddenGroup = Group("H", "n1", "n1"); hiddenGroup.Hidden = true;

        var vm = Vm();
        await vm.RefreshAsync(Resp(Group("G", "n1", "n1", "n2"), Node("n1"), hiddenNode, hiddenGroup), null);

        Assert.Equal(new[] { "G" }, vm.Groups.Select(g => g.Name));
        Assert.Equal(new[] { "n1" }, vm.Groups[0].Nodes.Select(n => n.Name));
    }

    // ---- 下面两条钉的是 Task 1 raw 响应里的两个形状事实（`.superpowers/sdd/.../probe-run1-raw.txt`）：
    //      API 的 type 是 Go 的 "Selector"/"URLTest"，不是配置文件里的 select/url-test；
    //      非成员条目的 provider-name 是**空串**而不是缺键。两条都会让上面的用例「全绿但真机全废」。

    [Fact]
    public async Task GroupsAreDetectedByMembers_AndProviderMembersMayBeAbsentFromTopLevel()
    {
        // 实测：`/proxies` 顶层只有组与内置项，provider 成员只出现在组的 `all` 和 `/providers/proxies` 里；
        // 按"顶层查不到就丢弃"实现的话，订阅节点会一条都不显示。分组也不能靠 type 名（实测是 "Selector"）。
        var selector = new ProxyDetail { Name = "PROBE-SELECT", Type = "Selector", Now = "direct-node", All = new() { "direct-node", "prov-node-1" } };
        var direct = new ProxyDetail { Name = "direct-node", Type = "Direct", ProviderName = "", Hidden = false };
        var member = new ProxyDetail { Name = "prov-node-1", Type = "Shadowsocks", ProviderName = "PROBE-PROV" };

        var measured = new List<string>();
        var vm = Vm(measure: (n, ct) => { measured.Add(n); return Task.FromResult(10); });
        await vm.RefreshAsync(Resp(selector, direct), new ProvidersResponse(new Dictionary<string, ProviderInfo>
        {
            ["PROBE-PROV"] = new() { Name = "PROBE-PROV", VehicleType = "File", Proxies = new() { member } },
        }));

        Assert.Equal(new[] { "PROBE-SELECT" }, vm.Groups.Select(g => g.Name));
        Assert.Equal(new[] { "direct-node", "prov-node-1" }, vm.Groups[0].Nodes.Select(n => n.Name));
        Assert.Null(vm.Groups[0].Nodes[0].ProviderName);           // "provider-name":"" 不是成员
        Assert.Equal("PROBE-PROV", vm.Groups[0].Nodes[1].ProviderName);

        await vm.TestAllAsync(default);
        Assert.Equal(new[] { "direct-node" }, measured);            // 空串成员必须可测，真成员必须不测（404）
    }

    [Fact]
    public async Task SampleFromProvidersWins_WhenTopLevelEntryCarriesNothing()
    {
        // 同名成员会同时出现在真 provider 与各组的 Compatible 伪 provider 里（probe 的 default 表就是这样），
        // 后者不带 provider-name 也不带样本。"先到先赢"会让真成员被伪条目占位，退化成不可测也读不到延迟。
        var top = new ProxyDetail { Name = "PROBE-SELECT", Type = "Selector", Now = "n1", All = new() { "n1" } };   // 顶层只有组
        var fake = new ProxyDetail { Name = "n1", Type = "Shadowsocks", ProviderName = "", History = new() };
        var real = new ProxyDetail { Name = "n1", Type = "Shadowsocks", ProviderName = "sub-a",
                                     History = new() { new DelaySample("t", 44, null) } };

        var vm = Vm();
        await vm.RefreshAsync(Resp(top), new ProvidersResponse(new Dictionary<string, ProviderInfo>
        {
            ["default"] = new() { Name = "default", VehicleType = "Compatible", Proxies = new() { fake } },
            ["sub-a"] = new() { Name = "sub-a", VehicleType = "Http", Proxies = new() { real } },
        }));

        Assert.Equal("sub-a", vm.Groups[0].Nodes[0].ProviderName);
        Assert.Equal("上次 44 ms", vm.Groups[0].Nodes[0].DelayText);
    }
}
