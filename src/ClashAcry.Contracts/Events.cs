namespace ClashAcry.Contracts;

/// 四个事件名即全部事件。日志文本不单独成事件——它随 <see cref="CoreStateChanged.StderrTail"/> 下发，
/// 预检评审里那条已死的 "log" 事件不得复活（一个事件类型只有一种 payload 形状）。
public static class EventTypes
{
    public const string CoreStateChanged = "core.state", UpdateProgress = "core.update.progress",
        UpdateResult = "core.update.result", SettingsChanged = "settings.changed";
}
