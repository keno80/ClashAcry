using System.Text.Json;
using ClashAcry.Contracts;
using ClashAcry.Tray;
using Microsoft.Win32;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace ClashAcry.Core.Tests;

/// <summary>
/// 命令路由的回归锁。路由不依赖托盘句柄、不依赖 XAML，所以每条命令都能在无交互环境里跑真夹具。
/// [Collection("RegistryGuard")]（裁定 R-10h）：`sysproxy.set` 与 `core.stop` 会真写 HKCU 的系统代理，
/// 而本机那份 ProxyServer 属于别的软件——快照必须在夹具里取，集合标注表达「这条不许与别的注册表用例并发」。
/// </summary>
[Collection("RegistryGuard")]
public class TrayCommandRoutingTests
{
    private static Frame Cmd(string type, string id = "c1", JsonElement? payload = null) =>
        new("command", type, payload, 0, id, null, null);

    [Fact]
    public async Task Handshake_Replys_WithCoreStateAndSecret()
    {
        using var h = new TrayHarness();   // 假内核 + WireMock controller
        var reply = await h.Routing.Handle(Cmd(CmdTypes.Handshake));

        var hs = JsonUtil.Read<HandshakeReply>(reply!.Payload);
        Assert.Equal(h.Core.State, hs.State);
        Assert.Equal(h.Store.Load().Secret, hs.Secret);
        Assert.Equal("reply", reply.Kind);
    }

    [Fact]
    public async Task SysProxySet_True_CallsEnableWithMixedPortAddress()
    {
        // R-10h：快照在 TrayHarness 里取（EveryDeclaredCommand 的 sysproxy.set 分支同样写真注册表），
        // 这条用例只额外断言还原前的真值。地址来自 settings.MixedPort（夹具是随机空闲端口），不是硬编码 7897。
        using var h = new TrayHarness();
        await h.Routing.Handle(Cmd(CmdTypes.SysProxySet, "c1", JsonUtil.Element(new { enabled = true })));

        Assert.True(SystemProxy.IsEnabled());
        Assert.Equal($"127.0.0.1:{h.Store.Load().MixedPort}", SystemProxy.CurrentServer());
    }

    [Fact]
    public async Task SysProxy_EnableThenDisable_RestoresTheForeignProxyServer()
    {
        // R-20b（第四轮人工冒烟真机吃到）：Enable 覆盖掉的原值必须有人负责还回去。
        // 这条从路由打通到注册表：开 → 原值进 settings.json；关 → 两键回原值、凭证销账。
        using var h = new TrayHarness();
        const string foreignServer = "corp.example:8080";
        const string foreignBypass = "localhost;127.*";
        using (var k = Registry.CurrentUser.CreateSubKey(SystemProxy.InternetSettingsKey))
        {
            k.SetValue("ProxyServer", foreignServer, RegistryValueKind.String);
            k.SetValue("ProxyOverride", foreignBypass, RegistryValueKind.String);
        }
        var ours = $"127.0.0.1:{h.Store.Load().MixedPort}";

        await h.Routing.Handle(Cmd(CmdTypes.SysProxySet, "c1", JsonUtil.Element(new { enabled = true })));
        Assert.Equal(ours, SystemProxy.CurrentServer());                     // 前置：确实被我们覆盖了
        Assert.Equal(foreignServer, h.Store.Load().SysProxyBackup!.Server);  // 快照进的是真原值

        await h.Routing.Handle(Cmd(CmdTypes.SysProxySet, "c2", JsonUtil.Element(new { enabled = false })));
        Assert.False(SystemProxy.IsEnabled());
        Assert.Equal(foreignServer, SystemProxy.CurrentServer());            // 还原
        using var after = Registry.CurrentUser.OpenSubKey(SystemProxy.InternetSettingsKey);
        Assert.Equal(foreignBypass, (string?)after!.GetValue("ProxyOverride"));
        Assert.Null(h.Store.Load().SysProxyBackup);                          // 还完就销凭证，不留二次还原的诱饵
    }

    [Fact]
    public async Task SysProxy_EnableTwice_KeepsTheTrueOriginal_NotOurOwnLeftover()
    {
        // 反证（R-20b 的建档判据）：盘上此刻已是我们上一轮写的串（还原失败 / 用户连开两次），
        // 第二次 Enable 若照抄当前值建档，真原值就被"我们自己的地址"顶替——那条路走完就永久回不去了。
        using var h = new TrayHarness();
        using (var k = Registry.CurrentUser.CreateSubKey(SystemProxy.InternetSettingsKey))
            k.SetValue("ProxyServer", "corp.example:8080", RegistryValueKind.String);

        await h.Routing.Handle(Cmd(CmdTypes.SysProxySet, "c1", JsonUtil.Element(new { enabled = true })));
        Assert.Equal("corp.example:8080", h.Store.Load().SysProxyBackup!.Server);

        await h.Routing.Handle(Cmd(CmdTypes.SysProxySet, "c2", JsonUtil.Element(new { enabled = true })));
        Assert.Equal("corp.example:8080", h.Store.Load().SysProxyBackup!.Server);
    }

    [Fact]
    public async Task ModeSet_ForwardsToClient_AndRepliesOk()
    {
        using var h = new TrayHarness();
        var reply = await h.Routing.Handle(Cmd(CmdTypes.ModeSet, "c1", JsonUtil.Element(new { mode = "global" })));

        Assert.Null(reply!.Error);
        // R-10m：不赌 LogEntries 时序，按路径取（Task 6 夹具注释里那条实测教训）。
        var put = h.Wire.LogEntries.Select(l => l.RequestMessage!).Single(m => m.Path == "/configs");
        // 派发样例写的 `put.BodyMap!["mode"]` 在 WireMock.Net 2.20 上不存在（CS1061：IRequestMessage 没有 BodyMap），
        // 用本仓库 Task 6 已验证的读法：直接解析原文 body（这也顺带钉住「body 真的是一层 JSON」）。
        Assert.Equal("global", JsonDocument.Parse(put.Body!).RootElement.GetProperty("mode").GetString());
        // spec §4.2 实测：v1 统一用 PATCH /configs 切模式（派发样例括号里那句「PUT 是 mode 端点」与 spec 不符，
        // 路由只调 MihomoClient.SetModeAsync，方法名由客户端与 spec 决定）。
        Assert.Equal("PATCH", put.Method);
    }

    [Fact]
    public async Task UnknownCommand_RepliesWithError_NotThrow()
    {
        using var h = new TrayHarness();
        var reply = await h.Routing.Handle(Cmd("nope"));
        Assert.Equal("unknown command: nope", reply!.Error);
    }

    [Fact]
    public async Task CoreStart_IsSerialized_TwoConcurrentRequestsOneAttempt()
    {
        // R-10i 的重叠期闸门：第一个请求卡在 ControllerAlive 里（生命周期锁还在它手上），
        // 第二个请求此刻才进来——不加闸门时「第二个恰好在第一个还没探完时进来」是靠调度碰运气。
        using var h = new TrayHarness(gatedProbe: true);
        var t1 = h.Routing.Handle(Cmd(CmdTypes.CoreStart, "a"));
        await h.ProbeEntered.WaitAsync(TimeSpan.FromSeconds(10));

        var t2 = h.Routing.Handle(Cmd(CmdTypes.CoreStart, "b"));
        h.ReleaseProbeGate();
        var replies = await Task.WhenAll(t1, t2);

        Assert.Equal(1, h.Core.ConcurrentGuardHits);   // 第二个请求走守卫，不再重复 spawn（spec §3 禁令 1）
        Assert.Equal(1, h.Core.SpawnCount);            // 真判据：进程表里只多出一个内核
        Assert.Equal(1, h.Core.StartAttemptCount);     // 守卫那条路径连启动循环都没进过
        Assert.All(replies, r => Assert.Null(r!.Error));
        Assert.All(replies, r => Assert.True(r!.Payload!.Value.GetBoolean()));
    }

    [Fact]
    public async Task CoreStop_RepliesWithRealStopResult()
    {
        // 裁定 R-10b：StopAsync 声明改回 Task<bool> 之后，core.stop 的回包才有「真的停了」可表达。
        using var h = new TrayHarness();
        var start = await h.Routing.Handle(Cmd(CmdTypes.CoreStart));
        Assert.True(start!.Payload!.Value.GetBoolean());
        Assert.Equal(CoreState.Running, h.Core.State);

        var stop = await h.Routing.Handle(Cmd(CmdTypes.CoreStop));

        Assert.True(stop!.Payload!.Value.GetBoolean());
        Assert.Equal(CoreState.Stopped, h.Core.State);
        Assert.Null(h.Core.RunningProcessId);
    }

    [Fact]
    public async Task CoreUpdate_WhenVersionReadFails_StillRepliesUpdated_NotErrorFrame()
    {
        // 真机端到端（Task 11 Step 6）：换核成功后内核**不一定在跑**（换之前没跑就不起），路由那句裸调
        // GetVersionAsync 就在「内核已经换好」的当口把 connection refused 抛穿到回帧——Updated 和
        // core.update.result 事件一起丢了，用户只看到一句 socket 错误。
        // 夹具用 500 表达「读不到版本」；非 2xx（ApiException）与断连（HttpRequestException）
        // 是同一条守卫的两个面，真机踩到的是后者。
        using var h = new TrayHarness();
        h.UpdateStubResult = UpdateResult.Updated;
        h.Wire.Reset();
        h.Wire.Given(Request.Create().WithPath("/version").UsingGet()).RespondWith(
            Response.Create().WithStatusCode(500));

        var reply = await h.Routing.Handle(Cmd(CmdTypes.CoreUpdate));

        Assert.Null(reply!.Error);
        Assert.Equal(UpdateResult.Updated, JsonUtil.Read<UpdateResult>(reply.Payload));
        Assert.NotEqual("v9.9.9-test", h.Store.Load().CoreVersion);   // 读失败就不许把旧版本号当成新值写进展示位
    }

    [Theory]
    [InlineData(CmdTypes.Handshake)]
    [InlineData(CmdTypes.CoreStart)] [InlineData(CmdTypes.CoreStop)]
    [InlineData(CmdTypes.CoreReload)] [InlineData(CmdTypes.ModeSet)]
    [InlineData(CmdTypes.SysProxySet)] [InlineData(CmdTypes.SettingsUpdate)] [InlineData(CmdTypes.SettingsGet)]
    [InlineData(CmdTypes.UiFocus)] [InlineData(CmdTypes.CoreUpdate)] [InlineData(CmdTypes.SubImport)]
    public async Task EveryDeclaredCommand_HasAHandler(string cmd)
    {
        using var h = new TrayHarness();
        var reply = await h.Routing.Handle(new Frame("command", cmd, h.PayloadFor(cmd), 0, "c1", null, null));

        Assert.NotNull(reply);                       // 不许有命令静默落到 unknown
        Assert.NotEqual("unknown command: " + cmd, reply!.Error);
    }

    [Fact]
    public async Task SubImport_MissingUrl_RepliesError_NotSilentSuccess()
    {
        using var h = new TrayHarness();
        var reply = await h.Routing.Handle(Cmd(CmdTypes.SubImport, "c2"));

        Assert.Contains("url", reply!.Error);         // 缺参数必须报错，不能假装导入成功（R-10g）
    }

    [Fact]
    public async Task SubImport_WritesRuntimeConfig_AndRepliesImportReply()
    {
        using var h = new TrayHarness();
        var reply = await h.Routing.Handle(Cmd(CmdTypes.SubImport, "c1",
            JsonUtil.Element(new ImportRequest(h.WireUrl + "/sub"))));

        var ir = JsonUtil.Read<ImportReply>(reply!.Payload);
        Assert.Null(ir!.Reason);                                            // Reason 为空即成功
        Assert.Equal(h.Paths.ConfigFile, ir.ConfigFile);                    // 只写 runtime\config.yaml
        Assert.True(File.Exists(h.Paths.ConfigFile));
    }

    [Fact]
    public async Task SettingsUpdate_ReplyCarriesSnapshotNotRawSettings()
    {
        // 裁定 R-10f（安全红线）：spec §4.1 只允许 secret 出现在 handshake 回包里。
        // ApplySettings 若返回 Settings，secret 与 core_path 就会原样序列化进帧——这条是它唯一的回归锁，
        // 所以断言的是**线格式原文**，不是「能反序列化成 SettingsSnapshot」（那个连泄露面都看不见）。
        using var h = new TrayHarness();
        var reply = await h.Routing.Handle(Cmd(CmdTypes.SettingsUpdate, "c1",
            JsonUtil.Element(new { mixed_port = 7899 })));

        Assert.Null(reply!.Error);
        var wire = reply.Payload!.Value.GetRawText();
        Assert.DoesNotContain("\"secret\"", wire);
        Assert.DoesNotContain("\"core_path\"", wire);
        Assert.DoesNotContain(h.Store.Load().Secret, wire);                 // 值本身也不许出现（键名改了也拦得住）

        var snap = JsonUtil.Read<SettingsSnapshot>(reply.Payload);
        Assert.Equal(7899, snap!.MixedPort);                                // 补丁真的落盘了
        Assert.Equal(7899, h.Store.Load().MixedPort);
        // 与 settings.changed 广播同一种形状（硬规矩③：一个事件类型只有一种 payload）
        var evt = new Frame("event", EventTypes.SettingsChanged, JsonUtil.Element(snap), 1, "e", null, null);
        Assert.Equal(wire, evt.Payload!.Value.GetRawText());
    }

    [Fact]
    public async Task SettingsGet_RepliesSnapshot_WithoutWritingAnything()
    {
        // R-14a：UI 起页要拿 sys_proxy_enabled，计划里只有 settings.update 回快照——用它做「读」就得发一次
        // no-op 补丁，代价是每次开界面都把引擎设置重写一遍（还顺带广播一次 settings.changed）。
        // 这条只读命令的判据就是「拿到同一种形状」+「一个字节都没落盘」。
        using var h = new TrayHarness();
        var before = File.GetLastWriteTimeUtc(h.Paths.SettingsFile);

        var reply = await h.Routing.Handle(Cmd(CmdTypes.SettingsGet));

        Assert.Null(reply!.Error);
        var wire = reply.Payload!.Value.GetRawText();
        Assert.DoesNotContain("\"secret\"", wire);                       // 与 settings.update 同一条红线（R-10f）
        Assert.DoesNotContain(h.Store.Load().Secret, wire);
        var snap = JsonUtil.Read<SettingsSnapshot>(reply.Payload);
        Assert.Equal(h.Store.Load().DelayTestUrl, snap!.DelayTestUrl);
        Assert.Equal(h.Store.Load().MixedPort, snap.MixedPort);
        Assert.Equal(before, File.GetLastWriteTimeUtc(h.Paths.SettingsFile));   // 读快照绝不落盘
    }

    [Fact]
    public async Task SettingsUpdate_DelayTestUrlOnly_IsAcceptedAndPersisted()
    {
        // 对派发样例的一处订正（证据见报告「偏离裁定」第 3 条）：样例的
        // `(p.MixedPort ?? 0) is var mp && (mp < 1 || …)` 把「这次没带这个字段」折成 0 再判坏，
        // 于是**只改 delay_test_url** 的合法补丁也会被「端口需在 1-65535」拒掉——而 delay_test_url
        // 恰恰是设置页唯一一个不带端口的补丁。更糟的是若按样例把 0 当合法值放过去，
        // `MixedPort = p.MixedPort ?? s.MixedPort` 会把 0 写进 settings，下次启动内核就绑 0 端口。
        // 这条用例锁住「按字段各判各的」这个实现：没带的字段既不校验也不改写。
        using var h = new TrayHarness();
        var portBefore = h.Store.Load().MixedPort;

        var reply = await h.Routing.Handle(Cmd(CmdTypes.SettingsUpdate, "c1",
            JsonUtil.Element(new { delay_test_url = "http://example.com/probe" })));

        Assert.Null(reply!.Error);                                        // 若样例的写法照抄，这里就是「端口需在 1-65535」
        Assert.Equal("http://example.com/probe", h.Store.Load().DelayTestUrl);
        Assert.Equal(portBefore, h.Store.Load().MixedPort);               // 没带的字段原样保留
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    public async Task SettingsUpdate_PortOutOfRange_RejectedAndNotPersisted(int port)
    {
        // 坏请求当场拒、绝不落盘（spec §5）；0 也在拒绝之列（样例把 0 当合法值放过）。
        using var h = new TrayHarness();
        var portBefore = h.Store.Load().MixedPort;

        var reply = await h.Routing.Handle(Cmd(CmdTypes.SettingsUpdate, "c1",
            JsonUtil.Element(new { mixed_port = port })));

        Assert.Contains("1-65535", reply!.Error);
        Assert.Equal(portBefore, h.Store.Load().MixedPort);
    }

    [Fact]
    public async Task UiFocus_RepliesAndInvokesInjectedAction_Once()
    {
        // 裁定 R-10e：路由的 ui.focus 分支调注入的 focusUi，而 focusUi 在夹具里是计数器。
        // 若实现把 ui.focus 接回 Routing.Handle（样例那句），本用例会以 StackOverflow 崩掉测试宿主的形式红；
        // 若实现偷偷自己 Process.Start 起 UI，就是越界（单测里起进程）——夹具的委托次数是这条的对照。
        using var h = new TrayHarness();
        var reply = await h.Routing.Handle(Cmd(CmdTypes.UiFocus));

        Assert.Equal(1, h.FocusUiCalls);
        Assert.Null(reply!.Error);
    }
}

/// <summary>
/// 裁定 R-10c：NIM_SETVERSION(4) 之后 lParam 的打包是 LOWORD=鼠标消息、HIWORD=uID。
/// 这两条是本任务唯一的托盘回归锁——Shell_NotifyIcon 真出现图标只能靠 Step 6 肉眼确认。
/// </summary>
public class TrayNotifyIconParamTests
{
    [Fact]
    public void DecodeTrayParam_KitV4_PacksMsgInLowWord()
    {
        var (iconId, mouseMsg) = Win32NotifyIcon.DecodeTrayParam(0x00020205);

        Assert.Equal(2, iconId);
        Assert.Equal(0x205, mouseMsg);          // WM_RBUTTONUP
    }

    [Fact]
    public void DecodeTrayParam_IgnoresHighBitsBeyondWord()
    {
        var (iconId, mouseMsg) = Win32NotifyIcon.DecodeTrayParam(0x10000_0203);

        Assert.Equal(0, iconId);                // 超出 uID 那个字的高位不参与
        Assert.Equal(0x203, mouseMsg);          // WM_LBUTTONDBLCLK
    }
}
