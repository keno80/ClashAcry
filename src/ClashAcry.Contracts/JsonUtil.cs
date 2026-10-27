using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClashAcry.Contracts;

/// <summary>
/// 集中一处放 JSON 选项，供各任务复用。类名不得叫 JsonSerializer（会遮蔽 System.Text.Json.JsonSerializer）。
/// </summary>
public static class JsonUtil
{
    // JsonStringEnumConverter 是硬要求：System.Text.Json 默认把枚举写成数字，
    // 而 core.update.result（UpdateResult）、core.state（CoreState）在 UI 侧要能与人手写的
    // "Updated" / "Running" 字面量互认，测试里也是按字符串发的。
    private static readonly JsonSerializerOptions Opts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public static JsonElement Element<T>(T value) => JsonSerializer.SerializeToElement(value, Opts);

    public static T Read<T>(JsonElement? element) =>
        element is null ? default! : element.Value.Deserialize<T>(Opts)!;
}
