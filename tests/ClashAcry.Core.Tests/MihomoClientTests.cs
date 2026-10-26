using System.Text.Json;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace ClashAcry.Core.Tests;

public class MihomoClientTests : IDisposable
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private readonly WireMockServer _wire = WireMockServer.Start();
    public MihomoClientTests() => _wire.Reset();
    public void Dispose() => _wire.Dispose();
    private MihomoClient Client() => new(Http, "127.0.0.1", _wire.Port, "sec");

    [Fact]
    public async Task GetProxies_ParsesProviderDashMember()
    {
        // spec §4.2 实测：provider 成员**不出现在** /proxies 顶层表（顶层只有组）；
        // 这里在同一个响应里多挂一条带 `provider-name` 的成员条目，逐字（raw JSON，不经过
        // 序列化器改名）钉死 ProxyDetail 对带横线键的映射——同一模型被 /providers/proxies
        // 的成员列表复用，Task 15 直接依赖这条映射。
        _wire.Given(Request.Create().WithPath("/proxies").UsingGet()).RespondWith(
            Response.Create().WithBody("""
            {"proxies":{
              "PROBE":{"name":"PROBE","type":"select","now":"n1","hidden":false,
                "all":["n1"],"history":[{"time":"t","delay":12}]},
              "prov-node-1":{"name":"prov-node-1","type":"ss","provider-name":"PROBE-PROV",
                "history":[{"time":"t2","delay":30,"number":1}]}
            }}
            """));

        var r = await Client().GetProxiesAsync(default);
        var g = r.Proxies["PROBE"];

        Assert.Equal("n1", g.Now);
        Assert.Equal(12, g.History![0].Delay);
        Assert.Equal("PROBE-PROV", r.Proxies["prov-node-1"].ProviderName);
    }

    [Fact]
    public async Task Delay_ReturnsMinusOne_On409()
    {
        _wire.Reset();
        _wire.Given(Request.Create().WithPath("/proxies/x/delay").UsingGet())
             .RespondWith(Response.Create().WithStatusCode(409).WithBody("unproxy"));
        Assert.Equal(-1, await Client().GetDelayAsync("x", "http://e/204", 5000, default));
    }

    [Fact]
    public async Task SetProxy_UsesBodyNameField_AndPathGroupName()
    {
        // 样例漏了这条映射：无 mapping 时 WireMock 回 404，客户端按契约抛 ApiException，走不到断言
        _wire.Given(Request.Create().WithPath("/proxies/PROBE").UsingPut())
             .RespondWith(Response.Create().WithStatusCode(204));
        await Client().SetProxyAsync("PROBE", "n1", default);
        // 夹具要求：LogEntries 里可能混有 WireMock admin 请求，先按 Path 过滤再取，不赌 .Last() 时序
        var req = _wire.LogEntries.Select(l => l.RequestMessage!).Last(m => m.Path == "/proxies/PROBE");
        Assert.Equal("/proxies/PROBE", req.Path);
        Assert.Equal("PUT", req.Method);
        Assert.Equal("n1", JsonDocument.Parse(req.Body!).RootElement.GetProperty("name").GetString());
        Assert.Equal("Bearer sec", req.Headers!["Authorization"]);
    }

    [Fact]
    public async Task ApiException_ExposesStatusCodeAndBody()
    {
        _wire.Reset();
        _wire.Given(Request.Create().WithPath("/version").UsingGet())
             .RespondWith(Response.Create().WithStatusCode(401).WithBody("unauthorized"));
        var ex = await Assert.ThrowsAsync<ApiException>(() => Client().GetVersionAsync(default));
        Assert.Equal(401, ex.StatusCode);
        Assert.Equal("unauthorized", ex.Body);
    }

    [Fact]
    public async Task GetConfigs_ParsesModeAndMixedPort()
    {
        // spec §4.2：/configs 实测 200，`mixed-port` 逐字证到，`mode` 在完整响应里但那次打印未证到
        // ——本用例把两者钉死；真机若取不到 mode，改模型并回写 spec，别动端点。
        _wire.Given(Request.Create().WithPath("/configs").UsingGet()).RespondWith(
            Response.Create().WithBody("""{"mode":"rule","mixed-port":7897,"port":0}"""));

        var c = await Client().GetConfigsAsync(default);

        Assert.Equal("rule", c!.Mode);
        Assert.Equal(7897, c.MixedPort);
    }

    [Fact]
    public async Task SharedHttpClient_KeepsBearer_PerRequest_AndLeaksNothingToOtherCallers()
    {
        // 裁定 R-6a：secret 属于「这一个请求」，不属于别人持有的 HttpClient。
        // Task 10 的 TrayHost 用同一个 HttpClient 跑 mihomo REST、订阅下载与 GitHub API，
        // 谁写 DefaultRequestHeaders 谁就把 controller secret 发给第三方机场。
        _wire.Given(Request.Create().WithPath("/proxies/PROBE").UsingPut())
             .RespondWith(Response.Create().WithStatusCode(204));   // 2.20 的 RespondWith 没有 int 重载
        _wire.Given(Request.Create().WithPath("/public").UsingGet())
             .RespondWith(Response.Create().WithBody("subscription-bytes"));

        await new MihomoClient(Http, "127.0.0.1", _wire.Port, "AAA").SetProxyAsync("PROBE", "n1", default);
        await Http.GetStringAsync($"{_wire.Url}/public");                       // 模拟订阅下载
        await new MihomoClient(Http, "127.0.0.1", _wire.Port, "BBB").SetProxyAsync("PROBE", "n2", default);

        // 按请求体里的节点名归位，不依赖 LogEntries 的时间顺序（不同版本新旧排列不一致）
        var puts = _wire.LogEntries.Select(l => l.RequestMessage!)
            .Where(m => m.Path == "/proxies/PROBE")
            .ToDictionary(m => JsonDocument.Parse(m.Body!).RootElement.GetProperty("name").GetString()!);
        Assert.Equal("Bearer AAA", puts["n1"].Headers!["Authorization"]);
        Assert.Equal("Bearer BBB", puts["n2"].Headers!["Authorization"]);        // 后一个客户端没被前一个盖住

        var plain = _wire.LogEntries.Select(l => l.RequestMessage!).Single(m => m.Path == "/public");
        Assert.DoesNotContain("Bearer", string.Join("|", plain.Headers!.Values)); // 下载里一个鉴权头都没有
    }

    [Fact]
    public async Task Delay_CallerCancellation_ThrowsInsteadOfBecomingMinusOne()
    {
        // 裁定 R-6b：-1 专指「这个节点测不出延迟」。取消是调用方的意图，吞成 -1 会让
        // Task 15 的轮询把「这次刷新被取消」渲染成「节点超时」——假的红色状态。
        _wire.Given(Request.Create().WithPath("/proxies/n1/delay").UsingGet())
             .RespondWith(Response.Create().WithDelay(2000).WithBody("""{"delay":5}"""));
        using var cts = new CancellationTokenSource(millisecondsDelay: 100);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Client().GetDelayAsync("n1", "http://e/204", 5000, cts.Token));
    }
}
