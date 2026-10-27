namespace ClashAcry.Core;

/// <summary>
/// 材质降级的纯策略（放 Core 而不是 UI：UI 测试集里没有能创建 <c>Microsoft.UI.Xaml.Window</c> 的 STA 宿主，
/// 而这条判据恰恰是唯一能被完全驱动的部分——三个布尔开关就是它的全部输入）。
/// </summary>
public static class BackdropPolicy
{
    public static (BackdropKind Effective, bool Degraded, string Reason) Resolve(
        BackdropKind requested, bool systemAllowsTransparency, bool acrylicSupported, bool micaSupported) => requested switch
    {
        BackdropKind.Acrylic when !systemAllowsTransparency
            => (BackdropKind.Solid, true, "系统透明效果已关闭"),
        BackdropKind.Acrylic when !acrylicSupported
            => (BackdropKind.Solid, true, "当前设备不支持亚克力"),
        BackdropKind.Mica when !systemAllowsTransparency
            => (BackdropKind.Solid, true, "系统透明效果已关闭"),
        BackdropKind.Mica when !micaSupported
            => (BackdropKind.Solid, true, "当前设备不支持 Mica"),
        BackdropKind.Mica => (BackdropKind.Mica, false, ""),
        BackdropKind.Acrylic => (BackdropKind.Acrylic, false, ""),
        _ => (BackdropKind.Solid, false, ""),
    };

    /// <summary>生效档的中文展示名——设置页那行「当前实际生效：…」用它，UI 侧不再自己拼映射表。</summary>
    public static string DisplayName(BackdropKind kind) => kind switch
    {
        BackdropKind.Acrylic => "亚克力",
        BackdropKind.Mica => "Mica",
        _ => "纯色",
    };
}
