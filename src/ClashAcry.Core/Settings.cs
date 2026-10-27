using System.Text.Json.Serialization;

namespace ClashAcry.Core;

/// <summary>
/// 线格式 = spec §4.3 的 settings.json。键名逐字段显式标注，不靠命名策略推断（与 Task 3 对
/// `Frame`/`HandshakeReply` 的裁定同源）。形状取**扁平 snake_case**（`controller_host`/`controller_port`），
/// 不嵌套 `controller:{host,port}`：Task 8/10/11 全靠 `with` 改单个端口字段，嵌套会让每个改动点变成
/// `s with { Controller = s.Controller with { Port = p } }`；而 Task 10 的 `SettingsPatch` 本来就是扁平
/// `controller_port`，两处对齐才不留翻译层。spec §4.3 已按此回写。
/// </summary>
public sealed record Settings(
    [property: JsonPropertyName("controller_host")] string ControllerHost = "127.0.0.1",
    [property: JsonPropertyName("controller_port")] int ControllerPort = 9097,
    [property: JsonPropertyName("secret")] string Secret = "",
    [property: JsonPropertyName("mixed_port")] int MixedPort = 7897,
    [property: JsonPropertyName("core_path")] string? CorePath = null,
    [property: JsonPropertyName("delay_test_url")] string DelayTestUrl = "http://www.gstatic.com/generate_204",
    [property: JsonPropertyName("last_subscription")] string? LastSubscription = null,
    [property: JsonPropertyName("core_version")] string? CoreVersion = null,
    // R-20b：开启系统代理前抓的 HKCU 原值快照（null = 没快照过）。它是"欠用户一次还原"的凭证，
    // 不是引擎设置，所以绝不出现在 settings.get 的快照里（spec §3：secret/core_path 同样不外露）。
    [property: JsonPropertyName("sys_proxy_backup")] SystemProxy.Backup? SysProxyBackup = null);
