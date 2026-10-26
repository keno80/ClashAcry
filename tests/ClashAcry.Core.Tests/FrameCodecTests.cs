using System.Text.Json;
using ClashAcry.Contracts;
using Xunit;

namespace ClashAcry.Core.Tests;

public class FrameCodecTests
{
    [Fact]
    public void Encode_ProducesSingleLine_WhenPayloadContainsNewlinesAndQuotes()
    {
        var payload = JsonUtil.Element(new { msg = "line1\nline2\"quoted\"" });
        var line = FrameCodec.Encode(new Frame("command", CmdTypes.Handshake, payload, 7, "id-1", null, null));

        Assert.DoesNotContain('\n', line.TrimEnd('\n'));
        Assert.EndsWith("\n", line);
    }

    [Fact]
    public void RoundTrip_PreservesAllFields()
    {
        var original = new Frame("reply", EventTypes.CoreStateChanged,
            JsonUtil.Element(new { state = "Running" }), 42, "r-2", "c-9", null);

        var back = FrameCodec.Decode(FrameCodec.Encode(original).TrimEnd('\n'));

        Assert.Equal(original.Kind, back.Kind);
        Assert.Equal(original.Type, back.Type);
        Assert.Equal(original.Seq, back.Seq);
        Assert.Equal(original.ReplyTo, back.ReplyTo);
        Assert.Equal("Running", back!.Payload!.Value.GetProperty("state").GetString());
    }

    [Fact]
    public void Decode_RejectsFrameLongerThanMaxFrameBytes()
    {
        var tooLong = new string('x', FrameCodec.MaxFrameBytes + 1);
        Assert.Throws<FrameTooLongException>(() => FrameCodec.Decode(tooLong));
    }

    // —— 三条管道硬规矩（本任务存在的理由）：UI 侧靠这些字符串/键名反序列化，拼错就是运行时抛 —— //

    [Fact]
    public void JsonUtil_SerializesEnumsAsStrings()
    {
        var el = JsonUtil.Element(new { state = CoreState.Running, result = UpdateResult.Updated });

        // 若是默认的数字枚举，这里会是 3 / 0；断言必须是字符串字面量。
        Assert.Equal("Running", el.GetProperty("state").GetString());
        Assert.Equal("Updated", el.GetProperty("result").GetString());
        Assert.DoesNotContain("\"state\":3", el.GetRawText());
    }

    [Fact]
    public void Payload_WireSpelling_IsSnakeCase()
    {
        // ImportReply 用单个参数构造（Task 10 就这么调），可选字段缺省为 null 被 WhenWritingNull 省略。
        var reply = JsonUtil.Element(new ImportReply("cfg.yaml"));
        Assert.Equal("cfg.yaml", reply.GetProperty("config_file").GetString());
        Assert.False(reply.TryGetProperty("reason", out _));
        Assert.False(reply.TryGetProperty("ConfigFile", out _));

        // CoreStateChanged：枚举走字符串、列表键是 stderr_tail（历史名=内核输出环）。
        var changed = JsonUtil.Element(new CoreStateChanged(CoreState.Crashed, "boom", new[] { "l1", "l2" }));
        Assert.Equal("Crashed", changed.GetProperty("state").GetString());
        Assert.Equal("boom", changed.GetProperty("reason").GetString());
        Assert.Equal(2, changed.GetProperty("stderr_tail").GetArrayLength());
        Assert.False(changed.TryGetProperty("StderrTail", out _));

        var progress = JsonUtil.Element(new UpdateProgress(1024, 4096, 25));
        Assert.Equal(1024, progress.GetProperty("received").GetInt64());
        Assert.Equal(4096, progress.GetProperty("total").GetInt64());
        Assert.Equal(25, progress.GetProperty("pct").GetInt32());
    }

    [Fact]
    public void Payload_RoundTripsThroughJsonUtil()
    {
        var original = new CoreStateChanged(CoreState.Updating, null, new[] { "x" });
        var back = JsonUtil.Read<CoreStateChanged>(JsonUtil.Element(original));

        Assert.Equal(original.State, back.State);
        Assert.Null(back.Reason);
        Assert.Equal(new[] { "x" }, back.StderrTail);
    }
}
