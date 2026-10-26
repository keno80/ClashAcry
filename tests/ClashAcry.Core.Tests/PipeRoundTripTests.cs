using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using ClashAcry.Contracts;

namespace ClashAcry.Core.Tests;

public class PipeRoundTripTests
{
    // 假内核路由在这里不需要，测试用一个可配置的 handler；f.Type == "hang" 时**永不回包**，
    // 专门用来验 R-9d（服务端消失时 pending 请求必须失败，而不是挂到调用方超时）。
    private static Func<Frame, Task<Frame?>> EchoHandler => f =>
    {
        if (f.Type == "hang") return new TaskCompletionSource<Frame?>().Task;   // 永不回包
        if (f.Type != CmdTypes.Handshake) return Task.FromResult<Frame?>(null);
        return Task.FromResult<Frame?>(new Frame("reply", f.Type, JsonUtil.Element(new HandshakeReply(
            CoreState.Running, "127.0.0.1", 9097, "sec", 7897, "v1", "http://www.gstatic.com/generate_204")),
            1, f.Id, f.Id, null));
    };

    [Fact]
    public async Task Request_AwaitsReply_WithSameReplyToId()
    {
        await using var server = new PipeServer(EchoHandler);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await server.StartAsync(cts.Token);          // 就绪即返回（R-9a），不需要再 Task.Delay 等监听

        await using var client = new PipeClient();
        Assert.True(await client.WaitUpAsync(TimeSpan.FromSeconds(5)));

        var reply = await client.RequestAsync(CmdTypes.Handshake, null, cts.Token);

        var hs = JsonUtil.Read<HandshakeReply>(reply!.Payload);
        Assert.Equal(CoreState.Running, hs.State);
        Assert.Equal("sec", hs.Secret);
        Assert.Equal(reply.ReplyTo, reply.Id);
    }

    [Fact]
    public async Task Broadcast_ReachesEveryConnectedClient()
    {
        await using var server = new PipeServer(_ => Task.FromResult<Frame?>(null));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await server.StartAsync(cts.Token);

        await using var a = new PipeClient(); await using var b = new PipeClient();
        var gotA = new TaskCompletionSource<Frame>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gotB = new TaskCompletionSource<Frame>(TaskCreationOptions.RunContinuationsAsynchronously);
        // 原样例行不过 `a.Event += gotA.TrySetResult`：Event 是 Action<Frame>（要 void），
        // 而 TrySetResult 返回 bool → CS0407。语义不变，只加一层丢弃返回值的 lambda。
        a.Event += f => gotA.TrySetResult(f); b.Event += f => gotB.TrySetResult(f);
        Assert.True(await a.WaitUpAsync(TimeSpan.FromSeconds(5)));
        Assert.True(await b.WaitUpAsync(TimeSpan.FromSeconds(5)));

        server.Broadcast(EventTypes.CoreStateChanged, new CoreStateChanged(CoreState.Running, null, new[] { "l1" }));

        // 评审 Minor 4：原来这两句 await 没有期限——R-9b 的队列若被改回同步写，这条会「挂死整套
        // 串行用例」而不是变红。先用 WhenAny 夹一层时限，时限到了就断言失败（原断言一字未动）。
        Assert.Same(gotA.Task, await Task.WhenAny(gotA.Task, Task.Delay(5000)));
        Assert.Same(gotB.Task, await Task.WhenAny(gotB.Task, Task.Delay(5000)));
        var fromA = await gotA.Task; var fromB = await gotB.Task;
        Assert.Equal(EventTypes.CoreStateChanged, fromA.Type);
        Assert.Equal(EventTypes.CoreStateChanged, fromB.Type);
        Assert.Equal(fromA.Seq, fromB.Seq);   // 同一次广播，两个客户端各得一份
        Assert.Equal(CoreState.Running, JsonUtil.Read<CoreStateChanged>(fromA.Payload).State);
    }

    [Fact]
    public async Task Client_WaitsUp_ReturnsFalseWhenServerAbsent()
    {
        // 这条断言的是「没有服务端」，所以它跑的时候**任何** server 都不许活着：
        // 上面的用例必须 await using 释放自己的服务端，漏一个就把这条变成随机红。
        Assert.False(await new PipeClient().WaitUpAsync(TimeSpan.FromMilliseconds(300)));
    }

    [Fact]
    public async Task StartAsync_ReturnsBeforeItsFirstClientConnects()
    {
        // 裁定 R-9a：样例把 accept 循环的 Task 直接当 StartAsync 的结果返回，
        // 于是每一句 await server.StartAsync(...) 都会挂到 CTS 超时——三条用例全红不是实现者的错。
        await using var server = new PipeServer(EchoHandler);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var sw = Stopwatch.StartNew();

        await server.StartAsync(cts.Token);

        Assert.True(sw.ElapsedMilliseconds < 2000, $"StartAsync 没有及时返回（{sw.ElapsedMilliseconds} ms）");
        await using var client = new PipeClient();
        Assert.True(await client.WaitUpAsync(TimeSpan.FromSeconds(2)));   // 返回即已在监听
    }

    [Fact]
    public async Task ManyBroadcasts_ReachBothClients_InOrder_AndLinesNeverTear()
    {
        // 裁定 R-9b：广播与回复共用一条流。没有每客户端一条队列 + 单写任务时，
        // 并发写会把「一帧一行」撕开（半个 JSON 行），或让慢客户端把 Tray 的广播线程挂死。
        await using var server = new PipeServer(_ => Task.FromResult<Frame?>(null));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await server.StartAsync(cts.Token);

        await using var a = new PipeClient(); await using var b = new PipeClient();
        var qa = new System.Collections.Concurrent.ConcurrentQueue<long>();
        var qb = new System.Collections.Concurrent.ConcurrentQueue<long>();
        a.Event += f => qa.Enqueue(f.Seq); b.Event += f => qb.Enqueue(f.Seq);
        Assert.True(await a.WaitUpAsync(TimeSpan.FromSeconds(5)));
        Assert.True(await b.WaitUpAsync(TimeSpan.FromSeconds(5)));

        for (long i = 1; i <= 50; i++)
            server.Broadcast(EventTypes.SettingsChanged, new { seq = i });

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while ((qa.Count < 50 || qb.Count < 50) && DateTime.UtcNow < deadline) await Task.Delay(50);
        Assert.Equal(50, qa.Count); Assert.Equal(50, qb.Count);            // 一帧都没丢、也没半行
        Assert.Equal(qa.ToArray(), qb.ToArray());                          // 两个客户端看到同一个顺序
        // 评审 Minor 5：上一句只把两个客户端互比——两路同时被扰（例如每连接起了两个 drain writer）
        // 照样绿。这一句把「顺序一致」钉成「就是 1..50 这个全序」。
        Assert.Equal(Enumerable.Range(1, 50).Select(i => (long)i), qa.ToArray());
    }

    [Fact]
    public async Task GarbageLine_DisconnectsOnlyThatClient_AndServerKeepsServing()
    {
        // 裁定 R-9c：坏帧抛 JsonException / InvalidDataException，而 InvalidDataException 派生自
        // SystemException、不在 IOException 家族里，所以 Pump 必须三型并列。异常逃出 fire-and-forget
        // 的 Pump 就是无声失败。
        await using var server = new PipeServer(EchoHandler);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await server.StartAsync(cts.Token);

        using (var raw = new NamedPipeClientStream(".", ContractConstants.PipeName,
                       PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            await raw.ConnectAsync(2000);
            var w = new StreamWriter(raw, new UTF8Encoding(false)) { AutoFlush = true };
            await w.WriteAsync("{ not json\n");                            // 非法帧，服务端必须只踢掉这条
        }

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (server.ClientCount > 0 && DateTime.UtcNow < deadline) await Task.Delay(50);
        Assert.Equal(0, server.ClientCount);

        await using var good = new PipeClient();
        Assert.True(await good.WaitUpAsync(TimeSpan.FromSeconds(5)));
        Assert.NotNull(await good.RequestAsync(CmdTypes.Handshake, null, cts.Token));   // accept 循环还活着
    }

    [Fact]
    public async Task Request_FailsFast_WhenServerGoesAwayMidFlight()
    {
        // 裁定 R-9d ②：ReadLoop 退出时必须把 _pending 全部失败掉。
        // 不这么做，Tray 崩在 UI 一次轮询中间，那次 await 会挂到自己 ct 的超时——spec §9 禁止无反馈卡死。
        var server = new PipeServer(EchoHandler);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await server.StartAsync(cts.Token);
        await using var client = new PipeClient();
        Assert.True(await client.WaitUpAsync(TimeSpan.FromSeconds(5)));

        var inflight = client.RequestAsync("hang", null, cts.Token);       // handler 永不回包
        await Task.Delay(200);
        await server.DisposeAsync();                                       // 服务端凭空消失

        var winner = await Task.WhenAny(inflight, Task.Delay(2000));
        Assert.Same(inflight, winner);                                     // 2 s 内有结果，不是挂到 10 s
        await Assert.ThrowsAsync<IOException>(() => inflight);
    }

    [Fact]
    public async Task Request_WithoutConnect_ThrowsMeaningfulError()
    {
        // 裁定 R-9d ③：未连接就 RequestAsync 在原样例里是 _w! 的 NullReferenceException。
        await using var client = new PipeClient();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.RequestAsync(CmdTypes.Handshake, null, default));
        Assert.Contains("not connected", ex.Message);
    }

    [Fact]
    public async Task DisposeWhileIdle_StopsAccepting_AndCompletesFast()
    {
        // 裁定 R-9e（本轮实测订正）：`WaitForConnectionAsync` 在 .NET 10 **确实**观察取消令牌，
        // 所以 DisposeAsync 里的 `_cts.Cancel()` 本身就能让循环结束；「顺手 Dispose 掉正在等待的那台
        // 实例」保留的理由是**关掉句柄**——不关，管道对象还挂在命名空间里，后面那句
        // `WaitUpAsync(500ms)` 就会连上这台已无人服务的实例而返回真，「服务端已消失」就不成立了。
        var server = new PipeServer(EchoHandler);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await server.StartAsync(cts.Token);

        var stop = Task.Run(() => server.DisposeAsync().AsTask());
        var finished = await Task.WhenAny(stop, Task.Delay(2000));
        Assert.Same(stop, finished);
        await stop;

        await using var late = new PipeClient();
        Assert.False(await late.WaitUpAsync(TimeSpan.FromMilliseconds(500)));
    }

    [Fact]
    public void ServerPipe_AclGrantsCurrentUser_AndNothingElse()
    {
        // 裁定 R-9f（实测）：SetAccessControl 在 .NET 10 抛 UnauthorizedAccessException，
        // 读回会留下默认 5 条 Allow（含 S-1-1-0 Everyone）——那样本机任何用户都能 handshake 把
        // controller secret 读走。唯一正确入口是 NamedPipeServerStreamAcl.Create，
        // 这条断言就是「没有 Everyone」的回归锁。
        using var pipe = PipeServer.CreateServerPipe();
        // 原样例行不过编译，两处都是类型问题、与断言内容无关：
        // ① AuthorizationRuleCollection 直接交给 Assert.Single 会绑定到非泛型重载、读回 object（CS1061），
        //    必须 Cast 才走 Assert.Single<T>；② AccessControlType 住在 AccessRule 而不是基类
        //    AuthorizationRule（CS1061 实测）。Cast 成 AccessRule 后断言与裁定 R-9f 逐字一致。
        var rules = pipe.GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier));

        var rule = Assert.Single(rules.Cast<AccessRule>());
        Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
        Assert.Equal(WindowsIdentity.GetCurrent().User!, Assert.IsType<SecurityIdentifier>(rule.IdentityReference));
        // 评审 Minor 7：上面三句锁住了条数/类型/SID，却锁不住**权利掩码**——把这条 ACE 放宽成
        // FullControl、或收窄回缺 CreateNewInstance 的 0x12019B（裁定 R-9h① 的回归，症状是
        // 「第二个客户端永远连不上」）都还是绿的。
        // 本机实测：读回的值不等于传进去的表达式——PipeAccessRights 打成 ACE 掩码时 .NET 一律补上
        // SYNCHRONIZE(0x100000)，所以 ReadWrite 读回 0x12019B、ReadWrite|CreateNewInstance 读回 0x12019F。
        // 断言必须按读回形态精确写（把 Synchronize 算进来），否则这条锁从第一天就是红的；
        // 精确相等同时钉死了两个回归方向：放宽（多 WriteDac/WriteOwner/FullControl）与收窄（缺 0x4）。
        Assert.Equal(PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance | PipeAccessRights.Synchronize,
            ((PipeAccessRule)rule).PipeAccessRights);
    }

    [Fact]
    public async Task ConcurrentRequests_DoNotInterleaveFrames_AndBothGetTheirOwnReply()
    {
        // 评审 I-3 + Minor 8 的回归锁。Task 12 的 TrayLink 是「轮询 + 用户操作」两条路径并发：
        // 同一时刻两条 WaitUpAsync、随后 20 个并发 RequestAsync。修复前（先查后改、连接路径无闸门）
        // 两次探活都能连上，后一次覆盖 _pipe/_w/_cts：被覆盖的那条在服务端永久存活（ClientCount 虚高、
        // 广播重复投递），而两个 ReadLoop 都从字段读同一台管道 → 同一字节流上两个 StreamReader 互抢，
        // 帧被撕开（JsonException 被 R-9c 吃掉 = 连接被踢）或回复被另一个循环读走 = 请求永无回复。
        // 断言三件可观察的事：① 服务端只留 1 条连接；② handler 读到的 20 行各自都是合法帧（撕裂走不到这里）；
        // ③ 20 个回复各归各的 reply_to，一个不多一个不少。
        var seen = new System.Collections.Concurrent.ConcurrentQueue<Frame>();
        Func<Frame, Task<Frame?>> EchoEveryFrame = f =>
        {
            seen.Enqueue(f);   // 只可能是完整帧：半行会在 FrameCodec.Decode 里抛，连接当场被摘
            return Task.FromResult<Frame?>(new Frame("reply", f.Type, f.Payload, 0, f.Id, f.Id, null));
        };

        await using var server = new PipeServer(EchoEveryFrame);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await server.StartAsync(cts.Token);

        await using var client = new PipeClient();
        var up = await Task.WhenAll(client.WaitUpAsync(TimeSpan.FromSeconds(5)),
                                    client.WaitUpAsync(TimeSpan.FromSeconds(5)));   // 两条路径同时探活
        Assert.True(up[0] && up[1]);
        // 循环「接受连接 → 登记客户端」之间有一个极短的窗口，先有界地等它登记，再判数：
        // 并发探活只许养出一条连接（多于 1 就是 I-3 描述的僵尸连接）。
        var registerDeadline = DateTime.UtcNow.AddSeconds(5);
        while (server.ClientCount < 1 && DateTime.UtcNow < registerDeadline) await Task.Delay(20);
        Assert.Equal(1, server.ClientCount);

        var tasks = Enumerable.Range(1, 20)
            .Select(i => client.RequestAsync(CmdTypes.Handshake, new { seq = i }, cts.Token))
            .ToArray();
        var all = Task.WhenAll(tasks);
        Assert.Same(all, await Task.WhenAny(all, Task.Delay(10000)));   // 有回复丢失就红，不许挂到 ct 的 20 s
        var replies = await all;

        var seqs = replies.Select(r => r!.Payload!.Value.GetProperty("seq").GetInt32()).OrderBy(x => x).ToArray();
        Assert.Equal(Enumerable.Range(1, 20), seqs);                                  // 各归各的，一份不多一份不少
        Assert.Equal(20, replies.Select(r => r!.ReplyTo).Distinct().Count());         // 每个请求配到自己的 id
        Assert.All(replies, r => Assert.Equal(r!.Id, r.ReplyTo));                     // 回包 id == 请求 id（handler 原样 echo）

        var served = seen.ToArray();
        Assert.Equal(20, served.Length);                                              // 服务端读到的行数 == 请求数
        Assert.All(served, f => Assert.Equal(CmdTypes.Handshake, f.Type));            // 每行独立解析成功过
        Assert.Equal(1, server.ClientCount);                                          // 没有被并发覆盖掉的僵尸连接
    }
}
