using YamlDotNet.RepresentationModel;

namespace ClashAcry.Core.Tests;

public class ConfigInjectorTests
{
    private static readonly Settings S = new(ControllerHost: "127.0.0.1", ControllerPort: 9097,
        Secret: "abcd", MixedPort: 7897);

    [Fact]
    public void Inject_OverwritesOnlyRequiredKeys_AndKeepsUserOrdering()
    {
        var input = "mode: rule\n# 用户的注释\nproxies:\n  - name: a\n    type: ss\nport: 7890\n";

        var output = ConfigInjector.Inject(input, S);

        Assert.Contains("mode: rule", output);
        Assert.Contains("- name: a", output);
        Assert.Contains("external-controller: 127.0.0.1:9097", output);
        Assert.Contains("secret: abcd", output);
        Assert.Contains("allow-lan: false", output);
        Assert.Contains("unified-delay: true", output);
        Assert.Contains("port: 7890", output);          // 已有端口字段 → 不补 mixed-port
        Assert.DoesNotContain("mixed-port", output);
    }

    [Fact]
    public void Inject_AddsMixedPort_OnlyWhenNoPortFieldPresent()
    {
        var output = ConfigInjector.Inject("mode: rule\n", S);
        Assert.Contains("mixed-port: 7897", output);
    }

    [Fact]
    public void Inject_ReplacesExistingRequiredKeyInsteadOfDuplicating()
    {
        var output = ConfigInjector.Inject("secret: old\nallow-lan: true\n", S);

        Assert.Single(Match(output, "secret:"));
        Assert.Equal("secret: abcd", Match(output, "secret:").Single());
        Assert.Equal("allow-lan: false", Match(output, "allow-lan:").Single());
    }

    [Fact]
    public void Inject_ThrowsOnNonMappingRoot()
    {
        Assert.Throws<ConfigShapeException>(() => ConfigInjector.Inject("- just\n- a list\n", S));
    }

    [Fact]
    public void Inject_OnlyTouchesTopLevelKeys_NestedSameNameSurvives()
    {
        // DOM 而不是字符串替换的真正理由：`secret:` 也合法地出现在 proxy-providers 的子项里，
        // 行替换会把它一起改掉，导致 provider 拉取订阅用的凭据被无声替换。
        var input = "proxy-providers:\n  p1:\n    type: http\n    secret: nested-keep\n";

        var output = ConfigInjector.Inject(input, S);

        Assert.Contains("secret: nested-keep", output);
        Assert.Single(Match(output, "secret:"));                       // 只有顶层那一个被覆盖
        Assert.Equal("secret: abcd", Match(output, "secret:").Single());
    }

    [Fact]
    public void Inject_LeavesUncoveredSubtrees_StructurallyIdentical()
    {
        // Task 1 实测的两条 fatal 约束在这里可执行化：`use:` 列表与 provider 的 type/path/interval
        // 一旦被"顺手"展平、丢弃或补 url，用户订阅会起不来或误导用户。
        var input = "proxy-groups:\n  - name: G\n    type: select\n    use: [P]\n"
                  + "proxy-providers:\n  P:\n    type: file\n    path: p.yaml\n    interval: 86400\n"
                  + "dns:\n  enable: true\n  nameserver:\n    - 223.5.5.5\n";

        var output = ConfigInjector.Inject(input, S);

        foreach (var key in new[] { "proxy-groups", "proxy-providers", "dns" })
            Assert.Equal(Dump(Top(input, key)), Dump(Top(output, key)));
        Assert.DoesNotContain("url:", Dump(Top(output, "proxy-providers")));   // 不得替 file 型 provider 补 url
    }

    private static YamlNode Top(string yaml, string key)
    {
        var st = new YamlStream(); st.Load(new StringReader(yaml));
        return ((YamlMappingNode)st.Documents[0].RootNode).Children
            .First(c => ((YamlScalarNode)c.Key).Value == key).Value;
    }
    private static string Dump(YamlNode n)
    {
        var s = new StringWriter();
        // assignAnchors:false 是必需的：默认 true 会给**内容重复**的节点发随机锚点（`&1065887375`），
        // 夹具一旦有重复标量，结构比对就会因随机 ID 偶发红灯。Task 8/10 之后任何写 yaml 的地方同理。
        new YamlStream(new YamlDocument(n)).Save(s, assignAnchors: false);
        return s.ToString();
    }
    private static string[] Match(string text, string key) =>
        text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.StartsWith(key)).ToArray();
}
