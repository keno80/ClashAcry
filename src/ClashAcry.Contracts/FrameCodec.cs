using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClashAcry.Contracts;

/// <summary>
/// 线格式：一帧一行 UTF-8 JSON + '\n'。System.Text.Json 默认转义换行、不缩进，
/// 因此任何字段值都不可能引入裸换行——这是行分帧成立的前提，不要打开 WriteIndented。
/// </summary>
public static class FrameCodec
{
    public const int MaxFrameBytes = 1 << 20;
    private static readonly JsonSerializerOptions Opts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // JavaScriptEncoder.Default：转义换行/引号/非 ASCII——正是"一帧一行"成立的前提。
        // （System.Text.Json.Encodings.Default 在 .NET 10 里并不存在，用等价的 BCL 默认编码器。）
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Default
    };

    public static string Encode(Frame f) => JsonSerializer.Serialize(f, Opts) + "\n";

    public static Frame Decode(string line)
    {
        if (Encoding.UTF8.GetByteCount(line) > MaxFrameBytes) throw new FrameTooLongException(line.Length);
        return JsonSerializer.Deserialize<Frame>(line, Opts) ?? throw new InvalidDataException("empty frame");
    }

    public static async Task<Frame?> ReadLineAsync(StreamReader reader, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var line = await reader.ReadLineAsync(ct);
        return line is null ? null : Decode(line);
    }
}

// .NET 10 里 InvalidDataException 已 sealed，无法派生，所以 FrameTooLongException 直接以 IOException 为基类；
// 但只有「过长」这一类属于 IOException 家族：「空帧」抛的 InvalidDataException 派生自 SystemException，
// 所以 Task 9 的 PipeServer.Pump 必须 IOException / InvalidDataException / JsonException 三型并列（裁定 R-9c）。
public sealed class FrameTooLongException : IOException
{
    public FrameTooLongException(int chars) : base($"frame too long: {chars} chars") { }
}
