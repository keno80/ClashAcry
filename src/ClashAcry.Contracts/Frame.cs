using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClashAcry.Contracts;

public sealed record Frame(string Kind, string Type, JsonElement? Payload, long Seq,
                            string? Id, string? ReplyTo, string? Error);

public sealed record HandshakeReply(CoreState State, string Host, int Port, string Secret,
                                     int MixedPort, string? CoreVersion, string DelayTestUrl);

/// <summary>
/// sub.import 的请求体 / 回包：Reason 为空即成功；UI 直接把它当"行内结果"文案的数据源（spec §6 失败保留旧配置）。
/// </summary>
public sealed record ImportRequest([property: JsonPropertyName("url")] string? Url = null,
                                   [property: JsonPropertyName("path")] string? Path = null);
public sealed record ImportReply([property: JsonPropertyName("config_file")] string? ConfigFile,
                                 [property: JsonPropertyName("reason")] string? Reason = null);

/// <summary>
/// 以下三型是 <c>core.state</c> / <c>core.update.progress</c> / <c>core.update.result</c> 三种事件的唯一 payload
/// （Global Constraint：一个事件一种形状）。
/// <see cref="CoreStateChanged"/> 由 <c>CoreManager.Transition</c> 唯一出口产出（Task 8）、
/// <c>CoreUpdater</c> 的进度/结果由 Task 11 产出，两侧都只 import 不重复定义。
/// </summary>
public sealed record CoreStateChanged([property: JsonPropertyName("state")] CoreState State,
                                      [property: JsonPropertyName("reason")] string? Reason,
                                      [property: JsonPropertyName("stderr_tail")] IReadOnlyList<string> StderrTail);
public sealed record UpdateProgress([property: JsonPropertyName("received")] long Received,
                                    [property: JsonPropertyName("total")] long Total,
                                    [property: JsonPropertyName("pct")] int Pct);
public enum UpdateResult { Updated, AlreadyLatest, DownloadFailed, ExtractFailed, InUseRolledBack }
