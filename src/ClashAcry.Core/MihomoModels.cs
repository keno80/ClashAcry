using System.Text.Json.Serialization;
namespace ClashAcry.Core;

public sealed record VersionInfo([property: JsonPropertyName("meta")] bool Meta,
                                 [property: JsonPropertyName("version")] string Version);

public sealed record DelaySample([property: JsonPropertyName("time")] string? Time,
    [property: JsonPropertyName("delay")] int? Delay,
    [property: JsonPropertyName("number")] int? Number)
{ public int? Effective => Delay ?? Number; }

public sealed class ProxyDetail
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("now")] public string? Now { get; set; }
    [JsonPropertyName("all")] public List<string>? All { get; set; }
    [JsonPropertyName("hidden")] public bool Hidden { get; set; }
    [JsonPropertyName("icon")] public string? Icon { get; set; }
    [JsonPropertyName("provider-name")] public string? ProviderName { get; set; }
    [JsonPropertyName("history")] public List<DelaySample>? History { get; set; }
    [JsonPropertyName("delay")] public int? Delay { get; set; }
}
public sealed record ProxiesResponse([property: JsonPropertyName("proxies")]
    Dictionary<string, ProxyDetail> Proxies);
public sealed class ProviderInfo
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("vehicleType")] public string? VehicleType { get; set; }
    [JsonPropertyName("updatedAt")] public DateTime? UpdatedAt { get; set; }
    [JsonPropertyName("testUrl")] public string? TestUrl { get; set; }
    [JsonPropertyName("expectedStatus")] public string? ExpectedStatus { get; set; }
    // Task 1 实测：provider 对象上**没有** `history` 键，健康样本在每个成员的 `history`/`extra` 里。
    // 曾按 provider.history 兜底显示「上次」是错的，别加回来。
    [JsonPropertyName("proxies")] public List<ProxyDetail>? Proxies { get; set; }
}
public sealed record ProvidersResponse(
    // Task 1 实测（v1.19.33）：根键是复数 `providers`，值是「键为 provider 名」的对象表，不是数组。
    // 表里还混有每个策略组自动生成的 `vehicleType:"Compatible"` 伪 provider 与 `default`：
    // 真实订阅一律按 `vehicleType` 过滤（只留 File / Http），否则节点页会出现一堆假 provider。
    [property: JsonPropertyName("providers")] Dictionary<string, ProviderInfo> Providers);
public sealed record CoreConfigView([property: JsonPropertyName("mode")] string? Mode,
    [property: JsonPropertyName("mixed-port")] int? MixedPort);
