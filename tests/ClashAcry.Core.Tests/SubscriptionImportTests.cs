using System.Text;
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
