using System.Text;
using WireMock.Matchers;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace ClashAcry.Core.Tests;

public class SubscriptionImportTests : IDisposable
{
    private readonly string Dir = Directory.CreateTempSubdirectory("ca-sub-").FullName;
    private readonly HttpClient Http = new();
    public void Dispose() { Http.Dispose(); Directory.Delete(Dir, true); }

    private (AppPaths P, SettingsStore S) Fixture(string original)
    {
        var p = AppPaths.ForTest(Dir); p.EnsureCreated();
        File.WriteAllText(p.ConfigFile, original);                 // 先有一份可用的旧配置
        return (p, new SettingsStore(p));
    }

    [Fact]
    public void ImportFromUrl_SendsAUserAgent_SoUaSniffingPanelsDoNotFourZeroThree()
    {
        // 真机现象（2026-10-27，用户机场）：面板对**没有 User-Agent 头**的请求一律 403。而 .NET 的裸
        // `new HttpClient()` 根本不发 UA，于是导入失败文案成了「订阅下载失败：… 403 (Forbidden)」。
        // 值同样是指令的一部分：自家 `ClashAcry/1.0` 会拿到 200 + `proxies: []` 的空模板，
        // `clash.meta` 才给完整节点列表——所以断言锁的是常量本身，不是「有 UA」这件事。
        var yaml = "mode: rule\nproxies:\n  - { name: n1, type: ss, server: 1.2.3.4 }\n";
        var wire = WireMockServer.Start();
        wire.Given(Request.Create().WithPath("/sub").UsingGet().WithHeader("User-Agent", new RegexMatcher(".+")))
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(yaml, "text/plain"));
        wire.Given(Request.Create().WithPath("/sub").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(403).WithBody("forbidden"));
        var (p, s) = Fixture("mode: old\n");

        var r = new SubscriptionImport(p, s).ImportFromUrl(wire.Url + "/sub", Http, default);

        Assert.Equal("", r.ReasonIfFailed);
        var req = wire.LogEntries.Select(l => l.RequestMessage!).Single(m => m.Path == "/sub");
        Assert.Equal(SubscriptionImport.UserAgent, string.Join("|", req.Headers!["User-Agent"]));
        Assert.Equal("clash.meta", SubscriptionImport.UserAgent);
        // 裁定 R-6a：这个 HttpClient 在 Tray 里与 controller / GitHub 共用，UA 只能逐请求带。
        Assert.Empty(Http.DefaultRequestHeaders.UserAgent);
        wire.Dispose();
    }

    [Fact]
    public void Import_EmptyProxiesTemplate_FailsLoudly_WritesNothing()
    {
        // 真机踩到的第二种失败：面板认不出客户端时不给 403，而是回一份 `proxies: []` 的完整模板
        // （自家 UA 10457 字节 0 个节点）。当成成功导入的话，界面显示「已导入」而节点页是空的。
        var body = "mode: rule\nproxies: []\nproxy-groups:\n  - { name: \u8282\u70b9, type: select, proxies: [DIRECT] }\n";
        var wire = WireMockServer.Start();
        wire.Given(Request.Create().WithPath("/sub").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(body, "text/plain"));
        var (p, s) = Fixture("mode: old\n");

        var r = new SubscriptionImport(p, s).ImportFromUrl(wire.Url + "/sub", Http, default);

        Assert.Equal(SubscriptionImport.EmptySubscriptionReason, r.ReasonIfFailed);
        Assert.Empty(Directory.GetFiles(p.ImportsDir, "*.yaml"));              // 连原件都不落，别留一份没用的订阅
        Assert.Equal("mode: old\n", File.ReadAllText(p.ConfigFile));           // 旧配置一个字节不动（spec §9）
        wire.Dispose();
    }

    [Fact]
    public void Import_ProvidersOnlyConfig_IsAccepted()
    {
        // 反证：机场用 proxy-providers 分发（没有 proxies 键或 proxies 为空）时不该被空模板判据误伤。
        var yaml = "mode: rule\nproxies: []\nproxy-providers:\n  p1: { type: http, url: http://x/y }\n";
        var (p, s) = Fixture("mode: old\n");

        var r = new SubscriptionImport(p, s).ImportFromText(yaml);

        Assert.Equal("", r.ReasonIfFailed);
    }

    [Fact]
    public void Import_DownloadsFails_KeepsOldConfigByteIdentical()
    {
        var wire = WireMockServer.Start();
        wire.Given(Request.Create().WithPath("/sub").UsingGet()).RespondWith(Response.Create().WithStatusCode(500));
        var (p, s) = Fixture("mode: rule\nsecret: keep\n");

        var r = new SubscriptionImport(p, s).ImportFromUrl(wire.Url + "/sub", Http, default);

        Assert.Equal("mode: rule\nsecret: keep\n", File.ReadAllText(p.ConfigFile));   // 契约 3：绝不清空
        Assert.NotEmpty(r.ReasonIfFailed);
        wire.Dispose();
    }

    [Fact]
    public void Import_ArchivesRawSourceBeforeInjecting()
    {
        // 契约 1：用户原文只进 imports\，注入产物只进 runtime\config.yaml；原件永不被改（红线）
        var yaml = "mode: rule\nmixed-port: 7890\nproxies:\n  - { name: n1, type: ss, server: 1.2.3.4 }\n";
        var wire = WireMockServer.Start();
        wire.Given(Request.Create().WithPath("/sub").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(yaml, "text/plain"));
        var (p, s) = Fixture("mode: old\n");

        var r = new SubscriptionImport(p, s).ImportFromUrl(wire.Url + "/sub", Http, default);

        Assert.Equal("", r.ReasonIfFailed);
        var raw = Directory.GetFiles(p.ImportsDir, "*.yaml");
        Assert.Single(raw);                                        // 存档恰好一份，文件名含 sha8 前缀
        Assert.Equal(yaml.Replace("\r\n", "\n"), File.ReadAllText(raw[0]).Replace("\r\n", "\n"));  // 原样，未注入
        Assert.Equal(p.ConfigFile, r.ConfigFile);
        Assert.Contains("external-controller", File.ReadAllText(p.ConfigFile));                    // 产物才带注入键
        wire.Dispose();
    }

    [Fact]
    public void Import_RejectsNodeLineList_WritesNothing()
    {
        // 契约 2：ss:// 行列表是 v1 明确不做（spec §6），必须报错且一个文件都不落
        var body = "ss://AAA@1.2.3.4:8389#hk1\nvmess://BBB#jp2\n";
        var (p, s) = Fixture("mode: old\n");

        var r = new SubscriptionImport(p, s).ImportFromText(body);

        Assert.Contains("不支持节点行导入", r.ReasonIfFailed);
        Assert.Equal("mode: old\n", File.ReadAllText(p.ConfigFile));                 // 旧配置不动
        Assert.False(Directory.Exists(p.ImportsDir) && Directory.GetFiles(p.ImportsDir).Length > 0);  // 连存档都不写
    }

    [Fact]
    public void Import_Base64OfClashYaml_ExpandsArchivesAndInjects()
    {
        // 机场订阅链接的主流形态：整份 base64，解开才是 Clash yaml。
        // 断言存档与产物都基于**解码后**的文本——存档存 base64 原文的话，用户下次打开存档是一堆乱码。
        const string ClashYaml = "mode: rule\nproxy-providers:\n  P:\n    type: file\n    path: p.yaml\nproxies:\n  - { name: n1, type: ss, server: 1.2.3.4 }\n";
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(ClashYaml));
        var (p, s) = Fixture("mode: old\n");

        var r = new SubscriptionImport(p, s).ImportFromText(b64);

        Assert.Equal("", r.ReasonIfFailed);
        var archived = File.ReadAllText(Directory.GetFiles(p.ImportsDir, "*.yaml").Single());
        Assert.Equal(ClashYaml, archived);                                            // 解码后的原文进存档
        var cfg = File.ReadAllText(p.ConfigFile);
        Assert.Contains("external-controller", cfg);
        Assert.DoesNotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes("mode: rule")), cfg);  // 产物里没有 base64 残片
    }

    [Fact]
    public void Import_Base64OfNodeLines_RejectedAsNodeList_WritesNothing()
    {
        // 只有 base64 通路、没有解码，用户看到的会是一句含糊的「不是 Clash yaml」；
        // 解开后必须落到与明文节点行同一条拒绝文案上。
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(
            "ss://AAA@1.2.3.4:8389#hk1\nvmess://BBB#jp2\n"));
        var (p, s) = Fixture("mode: old\n");

        var r = new SubscriptionImport(p, s).ImportFromText(b64);

        Assert.Contains("不支持节点行导入", r.ReasonIfFailed);
        Assert.Equal("mode: old\n", File.ReadAllText(p.ConfigFile));
        Assert.Empty(Directory.GetFiles(p.ImportsDir));
    }

    [Fact]
    public void Import_NodeLinesWithColonInRemark_StillRejected_WritesNothing()
    {
        // 顶层键嗅探的回归锁（Task 5 评审提出）：真实节点行的备注常写成 `#name: hk`，含冒号+空格。
        // 正则若退回宽松的 `[A-Za-z_-]+.*:\s`，这串会被当 Clash yaml 的顶层键，整份列表被存档并写进产物
        // ——spec §6 的「节点行导入不做」红线重新洞开且无声。
        var body = "ss://AAA@1.2.3.4:8389#name: hk1\nvmess://BBB#name: jp2\n";
        var (p, s) = Fixture("mode: old\n");

        var r = new SubscriptionImport(p, s).ImportFromText(body);

        Assert.Contains("不支持节点行导入", r.ReasonIfFailed);
        Assert.Equal("mode: old\n", File.ReadAllText(p.ConfigFile));
        Assert.Empty(Directory.GetFiles(p.ImportsDir));
    }

    [Fact]
    public void Import_Base64OfNodeLinesWithColon_StillRejected_WritesNothing()
    {
        // 同上，但走 base64 剥壳通路：解码后的判定必须与明文走同一条拒绝分支。
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(
            "ss://AAA@1.2.3.4:8389#name: hk1\ntrojan://pw@1.2.3.4:443#name: us2\n"));
        var (p, s) = Fixture("mode: old\n");

        var r = new SubscriptionImport(p, s).ImportFromText(b64);

        Assert.Contains("不支持节点行导入", r.ReasonIfFailed);
        Assert.Equal("mode: old\n", File.ReadAllText(p.ConfigFile));
        Assert.Empty(Directory.GetFiles(p.ImportsDir));
    }

    private string UrlSafeNoPad(string s) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    [Fact]
    public void Import_UrlSafeBase64WithoutPadding_StillExpands()
    {
        // 站点实际发的是 url-safe 字母表（`-`/`_`）且砍掉 `=` 填充；
        // Convert.FromBase64String 两种都不接受，不预处理就会把可用订阅误判成「既不是 yaml 也不是 base64」。
        const string ClashYaml = "mode: rule\nproxies:\n  - { name: n1, type: ss, server: 1.2.3.4 }\n";
        var (p, s) = Fixture("mode: old\n");

        var r = new SubscriptionImport(p, s).ImportFromText(UrlSafeNoPad(ClashYaml));

        Assert.Equal("", r.ReasonIfFailed);
        Assert.Equal(ClashYaml, File.ReadAllText(Directory.GetFiles(p.ImportsDir, "*.yaml").Single()));
    }

    [Fact]
    public void ImportFromFile_KeepsUserOriginalUntouched()
    {
        // 红线：永不回写用户手写的订阅/配置原件。这里用真文件走 ImportFromFile 通路，
        // 断言原件内容与 mtime 都不变，存档只是副本、产物另写 runtime\config.yaml。
        var userDir = Directory.CreateTempSubdirectory("ca-user-").FullName;
        try
        {
            var src = Path.Combine(userDir, "sub-original.yaml");
            var raw = "mode: rule\nmixed-port: 7890\nproxies:\n  - { name: n1, type: ss, server: 1.2.3.4 }\n";
            File.WriteAllText(src, raw);
            var before = File.GetLastWriteTimeUtc(src);
            var (p, s) = Fixture("mode: old\n");

            var r = new SubscriptionImport(p, s).ImportFromFile(src);

            Assert.Equal("", r.ReasonIfFailed);
            Assert.Equal(raw, File.ReadAllText(src));                                 // 原件一字未动
            Assert.Equal(before, File.GetLastWriteTimeUtc(src));                       // 也没被打开写过
            var archived = Directory.GetFiles(p.ImportsDir, "*.yaml").Single();
            Assert.NotEqual(src, archived);
            Assert.Equal(archived, r.OriginalFile);                                     // OriginalFile=存档副本，不是用户原件
            Assert.Equal(raw, File.ReadAllText(archived));                              // 存档是原样副本
            Assert.Contains("external-controller", File.ReadAllText(p.ConfigFile));    // 注入只进产物
        }
        finally { Directory.Delete(userDir, true); }
    }
}
