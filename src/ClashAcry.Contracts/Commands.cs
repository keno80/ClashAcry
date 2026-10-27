using System.Text.Json.Serialization;

namespace ClashAcry.Contracts;

public static class CmdTypes
{
    public const string Handshake = "handshake", CoreStart = "core.start", CoreStop = "core.stop",
        CoreReload = "core.reload", ModeSet = "mode.set", SysProxySet = "sysproxy.set",
        CoreUpdate = "core.update", SubImport = "sub.import",
        SettingsUpdate = "settings.update", SettingsGet = "settings.get", UiFocus = "ui.focus";
}

// Task 10：命令的**请求体**与 `settings.changed` 的**唯一载荷形状**都住在这里（不放 Tray 也不放 UI）——
// Tray 与 UI 要按同一形状编解码，放在任何一侧都会让另一侧抄一份（spec §4.1「一个事件类型只有一种 payload」）。
// 键名照 spec §4.3 的 settings.json 逐字对齐 snake_case，不靠命名策略推断。
public sealed record ModeRequest([property: JsonPropertyName("mode")] string Mode);

public sealed record SysProxyRequest([property: JsonPropertyName("enabled")] bool Enabled);

/// <summary>
/// `settings.update` 的请求：三个字段各自可空，**null = 不动这一项**。
/// 边界校验（端口 1-65535）由 Tray 的路由做，坏请求直接回错误、绝不落盘（spec §5）。
/// </summary>
public sealed record SettingsPatch([property: JsonPropertyName("mixed_port")] int? MixedPort,
                                   [property: JsonPropertyName("controller_port")] int? ControllerPort,
                                   [property: JsonPropertyName("delay_test_url")] string? DelayTestUrl);

/// <summary>
/// `settings.changed` 的唯一载荷形状：任何一次设置/系统代理变动都广播同一结构，
/// 不在同一个事件类型上挂两种形状（UI 侧就没法只靠 type 反序列化）。
/// **注意它不含 `secret`/`core_path`**：spec §4.1 红线是 secret 只在 `handshake` 回包里出现，
/// 所以 `settings.update` 的回包也必须是这个形状而不是 `Settings` 本身（裁定 R-10f）。
/// </summary>
public sealed record SettingsSnapshot([property: JsonPropertyName("sys_proxy_enabled")] bool SysProxyEnabled,
                                      [property: JsonPropertyName("mixed_port")] int MixedPort,
                                      [property: JsonPropertyName("delay_test_url")] string DelayTestUrl,
                                      [property: JsonPropertyName("core_version")] string? CoreVersion);
