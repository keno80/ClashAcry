using System.Text.Json;
using ClashAcry.Contracts;
using ClashAcry.UI.Tests.Fakes;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Exceptions;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace ClashAcry.UI.Tests;

/// <summary>
/// 节点页的窗口侧回归锁（Task 15）。手法：假 Tray（<see cref="FakeTray"/>）负责握手与命令回包，
/// 握手把 controller 地址指向 WireMock——界面的 REST 读于是落在一条可编程的假内核上。
/// 响应体一律写成 raw JSON，不匿名对象：真机键名带横线（<c>provider-name</c>），匿名类型写不出来。
/// 四条只有窗口能钉的判据：
/// ① <b>UIA RuntimeId 在轮询前后相同</b>——Core 的 <c>Assert.Same</c> 只保证 VM 实例复用，
///    容器有没有被重建是 WinUI 绑定层的事，只有窗口里的元素身份证能给答案；
/// ② 折叠只由尖角按钮承担（组行本体不设点击）；
/// ③ 切换的选中态只来自重拉后的 <c>now</c>（组行文本），界面不自己画勾；
/// ④ 全组测速绝不碰 <c>/proxies/{订阅成员}/delay</c>（真机 404，spec §4.2）——只有请求日志能证明。
/// </summary>
public class ProxiesNoJitterTests : IClassFixture<UiAppHarness>, IDisposable
{
    private readonly UiAppHarness _harness;
    private readonly WireMockServer _wire = WireMockServer.Start();

    public ProxiesNoJitterTests(UiAppHarness harness)
    {
        _harness = harness;
        _wire.Reset();
    }

    public void Dispose() => _wire.Dispose();

    [Fact]
    public async Task AutoRefresh_KeepsRowIdentity_AndUpdatesValueInPlace()
    {
        Stub(11);
        await using var tray = await FakeTrayWithCore();
        using var s = _harness.Launch("--reset-prefs");
        Assert.True(await tray.WaitUiConnectedAsync(TimeSpan.FromSeconds(10)));
        Show(s, "NavProxies");

        await WaitUntilAsync(() => Text(s, "Node_n1").Contains("11 ms"), 15);
        var id = RuntimeId(s, "Node_n1");

        Stub(99);
        await WaitUntilAsync(() => Text(s, "Node_n1").Contains("99 ms"), 15);

        // 文本换了，元素还是同一个：容器没被重建，滚动位置因此无从跳动。
        Assert.Equal(id, RuntimeId(s, "Node_n1"));
        Assert.False(s.Require("Node_n1").IsOffscreen);
    }

    [Fact]
    public async Task GroupCollapse_HidesMembers_ViaChevronOnly()
    {
        Stub(11);
        await using var tray = await FakeTrayWithCore();
        using var s = _harness.Launch("--reset-prefs");
        Assert.True(await tray.WaitUiConnectedAsync(TimeSpan.FromSeconds(10)));
        Show(s, "NavProxies");
        await WaitUntilAsync(() => Text(s, "Node_n1").Contains("11 ms"), 15);

        Press(s, "Node_G_Collapse");                                   // 只点尖角，不碰组行
        await WaitUntilAsync(() => Find(s, "Node_n1") is null, 8);
        Assert.NotNull(Find(s, "Group_G"));                            // 组行自己必须还在

        Press(s, "Node_G_Collapse");
        await WaitUntilAsync(() => Find(s, "Node_n1") is not null, 8);
    }

    [Fact]
    public async Task NodeClick_PutsToController_AndSelectionFollowsRefetchedNow()
    {
        Stub(11);
        _wire.Given(Request.Create().WithPath("/proxies/G").UsingPut())
             .RespondWith(Response.Create().WithStatusCode(204));
        await using var tray = await FakeTrayWithCore();
        using var s = _harness.Launch("--reset-prefs");
        Assert.True(await tray.WaitUiConnectedAsync(TimeSpan.FromSeconds(10)));
        Show(s, "NavProxies");
        await WaitUntilAsync(() => Text(s, "Node_n1").Contains("11 ms"), 15);

        Press(s, "Node_n2");
        await WaitUntilAsync(() => Paths("/proxies/G").Contains("PUT"), 8);

        // 桩此刻才把 now 改成 n2：界面在此之前不许显示选中，PUT 也不许被当成成功回执。
        Stub(11, now: "n2");
        await WaitUntilAsync(() => Text(s, "Group_G") == "G · n2", 15);
    }

    [Fact]
    public async Task TestAll_StampsInProgressBadge_AndNeverProbesProviderMembers()
    {
        // n2 是订阅成员：真机对它调 /proxies/n2/delay 一律 404，界面必须根本不发起这条请求。
        Stub(11);
        _wire.Given(Request.Create().WithPath("/proxies/n1/delay").UsingGet())
             .RespondWith(Response.Create().WithBody(JsonSerializer.Serialize(new { delay = 7 })));
        // 真机没有这条通路时的样子：留在桩里，万一实现走错就会拿到 404 而不是"没请求"。
        _wire.Given(Request.Create().WithPath("/proxies/n2/delay").UsingGet())
             .RespondWith(Response.Create().WithStatusCode(404).WithBody("""{"message":"Resource not found"}"""));

        await using var tray = await FakeTrayWithCore();
        using var s = _harness.Launch("--reset-prefs");
        Assert.True(await tray.WaitUiConnectedAsync(TimeSpan.FromSeconds(10)));
        Show(s, "NavProxies");
        await WaitUntilAsync(() => Text(s, "Node_n1").Contains("11 ms"), 15);
        Assert.Equal("n2 · 上次 33 ms", Text(s, "Node_n2"));   // 成员延迟只来自 providers 的样本，不冒充实时值

        Press(s, "TestAllButton");
        await WaitUntilAsync(() => Text(s, "Node_n1_Badge").Contains("测速中") || Text(s, "Node_n1").Contains("7 ms"), 8);
        await WaitUntilAsync(() => Text(s, "Node_n1").Contains("7 ms") && Text(s, "TestAllButton") == "全部测速", 20);

        Assert.Empty(Paths("/proxies/n2/delay"));                      // 一次都不许有
        Assert.Single(Paths("/proxies/n1/delay"));
        Assert.True(s.Require("TestAllButton").IsEnabled);              // 批量收尾后按钮必须重新可用
    }

    [Fact]
    public async Task ProviderRefresh_GoesToRegisteredEndpoint_AndRestoresWhenDone()
    {
        Stub(11);
        _wire.Given(Request.Create().WithPath("/providers/proxies/sub-a").UsingPut())
             .RespondWith(Response.Create().WithStatusCode(204).WithDelay(1_500));
        await using var tray = await FakeTrayWithCore();
        using var s = _harness.Launch("--reset-prefs");
        Assert.True(await tray.WaitUiConnectedAsync(TimeSpan.FromSeconds(10)));
        Show(s, "NavProxies");

        await WaitUntilAsync(() => Text(s, "Provider_sub-a").Contains("1 个节点"), 15);
        Press(s, "Provider_sub-a_Refresh");

        await WaitUntilAsync(() => Text(s, "Provider_sub-a_Refresh").Contains("刷新中")
                                 || !s.Require("Provider_sub-a_Refresh").IsEnabled, 5);
        await WaitUntilAsync(() => Text(s, "Provider_sub-a_Refresh") == "刷新"
                                 && s.Require("Provider_sub-a_Refresh").IsEnabled, 20);
        Assert.Contains("PUT", Paths("/providers/proxies/sub-a"));
    }

    // ---- 桩与读取工具 ---------------------------------------------------------------

    /// <summary>起一台假 Tray，并把握手的 controller 指到 WireMock：界面的 REST 读因此全部落在桩上。</summary>
    private async Task<FakeTray> FakeTrayWithCore()
    {
        var tray = await FakeTray.StartAsync();
        tray.Handshake = new HandshakeReply(CoreState.Running, "127.0.0.1", _wire.Port, "sec-test",
            18999, "v-test", $"{_wire.Url}/generate_204");
        tray.Snapshot = tray.Snapshot with { DelayTestUrl = $"{_wire.Url}/generate_204" };
        return tray;
    }

    /// <summary>
    /// <c>/proxies</c> 与 <c>/providers/proxies</c> 的形状照 Task 1 实测来：顶层只有组（type 是 Go 的
    /// "Selector"）与非订阅成员，订阅成员只出现在组的 <c>all</c> 与 providers 的成员表里。
    /// </summary>
    private void Stub(int delay, string now = "n1")
    {
        _wire.ResetMappings();
        _wire.Given(Request.Create().WithPath("/proxies").UsingGet())
             .RespondWith(Response.Create().WithBody($$"""
             {"proxies":{
               "G":{"name":"G","type":"Selector","now":"{{now}}","all":["n1","n2"],"hidden":false,"history":[]},
               "n1":{"name":"n1","type":"Shadowsocks","hidden":false,"provider-name":"","delay":{{delay}},"history":[]}
               }
             }
             """));
        _wire.Given(Request.Create().WithPath("/providers/proxies").UsingGet())
             .RespondWith(Response.Create().WithBody("""
             {"providers":{
               "sub-a":{"name":"sub-a","vehicleType":"Http","updatedAt":"2026-10-26T08:30:00+08:00",
                 "proxies":[{"name":"n2","type":"Shadowsocks","hidden":false,"provider-name":"sub-a",
                             "history":[{"time":"2026-10-26T08:00:00+08:00","delay":33}]}]},
               "default":{"name":"default","vehicleType":"Compatible","updatedAt":"0001-01-01T00:00:00Z","proxies":[]}
             }}
             """));
    }

    /// <summary>WireMock 的请求日志按路径取方法名；admin 请求不在这个路径上，无需再过滤。</summary>
    private List<string> Paths(string path) =>
        _wire.LogEntries.Select(l => l.RequestMessage!).Where(m => m.Path == path).Select(m => m.Method!).ToList();

    private static string Text(UiSession s, string id)
    {
        try { return s.Require(id).Name; }
        catch (PropertyNotSupportedException) { return ""; }        // 空文本的 TextBlock 在 UIA 里读不出 Name（Task 14 实测）
    }

    private static AutomationElement? Find(UiSession s, string id) =>
        s.Window.FindFirstDescendant(cf => cf.ByAutomationId(id));

    private static string RuntimeId(UiSession s, string id) => string.Join(",", s.Require(id).Properties.RuntimeId.Value);

    private static void Press(UiSession s, string id)
    {
        var e = s.Require(id);
        if (e.Patterns.Invoke.IsSupported) e.Patterns.Invoke.Pattern.Invoke();
        else if (e.Patterns.Toggle.IsSupported) e.Patterns.Toggle.Pattern.Toggle();
        else e.Click();
    }

    private static void Show(UiSession s, string navId)
    {
        s.Require(navId).Patterns.SelectionItem.Pattern.Select();
        var pageId = navId switch { "NavProxies" => "ProxiesPage", "NavSettings" => "SettingsPage", _ => "OverviewPage" };
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var page = s.Window.FindFirstDescendant(cf => cf.ByAutomationId(pageId));
            if (page is not null && !page.IsOffscreen) return;
            Thread.Sleep(100);
        }
        throw new Xunit.Sdk.XunitException($"切换后未见页面 {pageId}");
    }

    private static async Task WaitUntilAsync(Func<bool> cond, int seconds)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            try { if (cond()) return; } catch (Exception) { /* 界面正在重建这一帧，下一轮再读 */ }
            await Task.Delay(100);
        }
        throw new TimeoutException($"断言等待超时：{seconds} s 内条件未成立");
    }
}
