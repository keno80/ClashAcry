using YamlDotNet.RepresentationModel;

namespace ClashAcry.Core;

/// <summary>
/// 只覆盖顶层必需键。用 YamlStream/DOM 而非强类型模型，是为了保证订阅里我们不认识的字段
/// （geodata-mode、dns、experimental…）原样透传 —— spec §7「注入最小化」。
/// </summary>
public static class ConfigInjector
{
    /// <summary>注入器**无条件**写下的顶层键（spec §7）。`mixed-port` 在这份名单里是有理由的：
    /// 系统代理与内核探活用的都是设置里那个端口，订阅自带的端口透传进去就等于让两者指向不同的门。</summary>
    public const string RequiredKeys = "external-controller, secret, allow-lan, unified-delay, mixed-port";

    public static string Inject(string yaml, Settings s)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode root)
            throw new ConfigShapeException("配置根节点必须是单一 YAML 映射");

        var overrides = new Dictionary<string, YamlNode>(StringComparer.Ordinal)
        {
            ["external-controller"] = new YamlScalarNode($"{s.ControllerHost}:{s.ControllerPort}"),
            ["secret"] = new YamlScalarNode(s.Secret),
            ["allow-lan"] = new YamlScalarNode("false"),
            ["unified-delay"] = new YamlScalarNode("true"),
            // 真机踩到的洞（2026-10-27，用户机场）：订阅头部就是 `mixed-port: 7890`。
            // 早先的规则是「原文有任何端口键就不补 mixed-port」，于是内核听 7890，
            // 而 Tray 按设置把系统代理指向 7897、双探活也查 7897 —— 界面全绿、流量全断。
            ["mixed-port"] = new YamlScalarNode(s.MixedPort.ToString()),
        };

        // 未触及的键值对原样搬走（复用同一个 YamlNode 实例），只对必需键换值节点，
        // 这样键序与缩进风格都不受我们影响。
        var pairs = new List<KeyValuePair<YamlNode, YamlNode>>();
        foreach (var child in root.Children)
        {
            if (child.Key is not YamlScalarNode key)
                throw new ConfigShapeException($"顶层键必须是标量，实为 {child.Key.NodeType}");
            if (overrides.TryGetValue(key.Value ?? "", out var replacement))
            {
                pairs.Add(new KeyValuePair<YamlNode, YamlNode>(key, replacement));
                overrides.Remove(key.Value ?? "");
            }
            else pairs.Add(child);
        }
        foreach (var kv in overrides) pairs.Add(new KeyValuePair<YamlNode, YamlNode>(new YamlScalarNode(kv.Key), kv.Value));

        // YamlDotNet 18 的 YamlDocument.RootNode 只读，所以带着新根节点重建一个单文档流来 Save。
        // assignAnchors=false：默认 true 会给「内容重复的节点」（如多个 `type:` 键）发随机锚点
        // `&1313797819 type: direct`，用户看的产物里会长出他们没写过的东西。
        var sw = new StringWriter();
        new YamlStream(new YamlDocument(new YamlMappingNode(pairs))).Save(sw, assignAnchors: false);
        return sw.ToString();
    }
}

public sealed class ConfigShapeException : Exception
{
    public ConfigShapeException(string m) : base(m) { }
}
