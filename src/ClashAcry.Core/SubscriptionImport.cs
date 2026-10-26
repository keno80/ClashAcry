using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ClashAcry.Core;

public sealed record ImportResult(string OriginalFile, string ConfigFile, string ReasonIfFailed);

/// <summary>
/// 订阅通路：取字节（URL/本地文件）→ 剥编码外壳 → 嗅探 → 原文存档 → 注入 → 写产物。
/// 三条红线：原件只读、失败保留旧配置（spec §9）、节点行列表零落盘（spec §6）。
/// </summary>
public sealed class SubscriptionImport
{
    public const string NodeListRejectReason = "v1 不支持节点行导入（spec §6）";
    public const string UnrecognizedReason = "订阅内容既不是 Clash yaml 也不是可解的 base64";

    private readonly AppPaths _paths;
    private readonly SettingsStore _store;

    public SubscriptionImport(AppPaths paths, SettingsStore store)
    {
        _paths = paths; _store = store;
        _paths.EnsureCreated();
    }

    public ImportResult ImportFromUrl(string url, HttpClient http, CancellationToken ct)
    {
        string raw;
        try
        {
            // 取字节失败也走同一条失败语义：旧配置一个字节都不动。
            using var resp = http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
                .GetAwaiter().GetResult();
            resp.EnsureSuccessStatusCode();
            raw = new StreamReader(resp.Content.ReadAsStream(ct), Encoding.UTF8)
                .ReadToEnd();   // 解码交给 StreamReader，字节层不做任何转换
        }
        catch (Exception ex) { return Failed($"订阅下载失败：{ex.Message}"); }
        return Import(raw, "sub");
    }

    public ImportResult ImportFromFile(string path)
    {
        string raw;
        try { raw = File.ReadAllText(path); }
        catch (Exception ex) { return Failed($"读取本地订阅失败：{ex.Message}"); }
        return Import(raw, "file");
    }

    public ImportResult ImportFromText(string raw) => Import(raw, "sub");

    private ImportResult Import(string raw, string kind)
    {
        // BOM 先剥：U+FEFF 不算 whitespace，留着它 base64 判定与 yaml 嗅探都会走眼。
        raw = DecodeMaybeBase64(raw.TrimStart('﻿'));
        if (IsNodeLineList(raw)) return Failed(NodeListRejectReason);
        if (!LooksLikeClashYaml(raw)) return Failed(UnrecognizedReason);

        // 契约 1：原文（解码后）先原样进 imports\，产物才进 runtime\config.yaml。
        var archive = Path.Combine(_paths.ImportsDir, $"{kind}-{Sha8(raw)}.yaml");
        try { File.WriteAllText(archive, raw); }
        catch (Exception ex) { return Failed($"存档失败：{ex.Message}"); }

        string injected;
        try { injected = ConfigInjector.Inject(raw, _store.Load()); }
        // 注入失败发生在存档之后：旧配置原样留着，绝不写产物（spec §9「永不清空配置」）。
        catch (ConfigShapeException ex) { return Failed(ex.Message); }
        catch (YamlDotNet.Core.YamlException ex) { return Failed($"yaml 解析失败：{ex.Message}"); }

        try { WriteAtomic(_paths.ConfigFile, injected); }
        catch (Exception ex) { return Failed($"写入配置失败：{ex.Message}"); }
        return new ImportResult(archive, _paths.ConfigFile, "");
    }

    private ImportResult Failed(string reason) => new(_paths.ConfigFile, _paths.ConfigFile, reason);

    private static readonly Regex NodeLine = new(@"^\s*(ss|ssr|vmess|trojan|vless|hysteria2?)://",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>节点行列表：没有 `:` 形式的顶层 yaml 键，却有多行 `scheme://`。</summary>
    private static bool IsNodeLineList(string text)
    {
        var lines = text.Split('\n');
        return !TopLevelKey.IsMatch(text)
            && lines.Count(l => NodeLine.IsMatch(l)) >= 2;
    }

    // 顶层 yaml 键必须是「标识符 + : + 空白/行尾」。简报给的 `[A-Za-z_-]+.*:\s` 里那个 `.*`
    // 会让 `ss://h:1#name: hk` 这类节点行冒充键，整份节点行列表被当 Clash yaml 写进产物（spec §6 明令拒绝）。
    private static readonly Regex TopLevelKey = new(@"^[A-Za-z_-][A-Za-z0-9_-]*:(?=\s|$)",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static bool LooksLikeClashYaml(string text) => TopLevelKey.IsMatch(text);

    /// <summary>整份是 base64（机场订阅的常见分发形态）就解开；解出来必须是可打印文本，否则原样返回交给后续判断。</summary>
    private static string DecodeMaybeBase64(string raw)
    {
        var compact = new string(raw.Where(c => !char.IsWhiteSpace(c)).ToArray());
        // 长度 %4==1 绝不是 base64；缺 `=` 填充（%4 为 2 或 3）是机场的常态，不能在这里挡掉，交给 Pad 补。
        // yaml 一定含 `:`，不在字母表里，所以真 yaml 不会被误当 base64。
        if (compact.Length == 0 || compact.Length % 4 == 1 || !compact.All(IsB64Char)) return raw;
        try
        {
            var text = Encoding.UTF8.GetString(Convert.FromBase64String(Pad(compact)));
            return text.All(c => char.IsWhiteSpace(c) || !char.IsControl(c)) ? text : raw;
        }
        catch (FormatException) { return raw; }
    }

    private static bool IsB64Char(char c) => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9'
        or '+' or '/' or '-' or '_' or '=';

    /// <summary>url-safe 字母表（`-`/`_`）与缺 `=` 填充都在这儿归一。</summary>
    private static string Pad(string compact)
    {
        var s = compact.Replace('-', '+').Replace('_', '/');
        return s.TrimEnd('=') + new string('=', (4 - s.TrimEnd('=').Length % 4) % 4);
    }

    private static string Sha8(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant()[..8];

    private static void WriteAtomic(string target, string text)
    {
        // 内核随后要读这个文件，半截写入等于把用户推到启动失败上（spec §9）。
        var tmp = Path.Combine(Path.GetDirectoryName(target)!, $".tmp-{Guid.NewGuid():N}.yaml");
        try
        {
            File.WriteAllText(tmp, text);
            if (File.Exists(target)) File.Replace(tmp, target, null);
            else File.Move(tmp, target);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { }
        }
    }
}
