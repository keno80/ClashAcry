namespace ClashAcry.Contracts;

public static class CmdTypes
{
    public const string Handshake = "handshake", CoreStart = "core.start", CoreStop = "core.stop",
        CoreReload = "core.reload", ModeSet = "mode.set", SysProxySet = "sysproxy.set",
        CoreUpdate = "core.update", SubImport = "sub.import",
        SettingsUpdate = "settings.update", UiFocus = "ui.focus";
}
