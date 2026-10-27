using ClashAcry.Core;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;

namespace ClashAcry.UI.Backdrops;

/// <summary>
/// 把 <see cref="BackdropPolicy"/> 的结果装到窗口上——判级逻辑一行都不在这里（那是 Core 的纯函数），
/// 这里只负责「装哪块 backdrop」和三处注入点。
/// 三个 <see cref="Func{TResult}"/> 是**降级分支的可驱动面**：真机上「关掉系统透明效果」要改用户的个性化设置，
/// 测试不许动别人的机器，所以那条分支由注入假开关驱动（R-13a）。
/// </summary>
public sealed class BackdropService
{
    private readonly Func<bool> _systemAllows, _acrylicOk, _micaOk;

    public BackdropService(Func<bool>? systemAllows = null, Func<bool>? acrylicOk = null, Func<bool>? micaOk = null)
    {
        _systemAllows = systemAllows ?? TransparentEffectsAllowedBySystem;
        _acrylicOk = acrylicOk ?? DesktopAcrylicController.IsSupported;
        _micaOk = micaOk ?? MicaController.IsSupported;
    }

    public BackdropKind Requested { get; private set; } = BackdropKind.Acrylic;      // spec §8：默认亚克力
    public BackdropKind Effective { get; private set; } = BackdropKind.Solid;
    public bool Degraded { get; private set; }
    public string DegradedReason { get; private set; } = "";

    /// <summary>设置页那行的原文：降级时把原因带在括号里（spec §8「降级要明说」）。</summary>
    public string EffectiveLabel => Degraded
        ? $"当前实际生效：{BackdropPolicy.DisplayName(Effective)}（{DegradedReason}）"
        : $"当前实际生效：{BackdropPolicy.DisplayName(Effective)}";

    /// <summary>
    /// R-13b：装的是 XAML 层的 <see cref="Microsoft.UI.Xaml.Media.DesktopAcrylicBackdrop"/>/<c>MicaBackdrop</c>
    /// （<c>Window.SystemBackdrop</c> 的属性类型就是它们的基类 <c>SystemBackdrop</c>），
    /// 不是计划样例里的 <c>DesktopAcrylicController</c>：那两个 composition 控制器在 2.3.9 的 .NET 投影里
    /// 既不能赋给 <c>SystemBackdrop</c>（CS0029），<c>AddSystemBackdropConfiguration</c> 也不可见（CS1061）——
    /// 它们实现的那个接口 <c>Microsoft.UI.Xaml.Media.ISystemBackdrop</c> 是不可访问类型（CS0122）。
    /// 走 XAML 层还顺带省掉 <c>SystemBackdropConfiguration</c> 与 <c>Activated</c> 挂钩：明暗自动跟根元素的
    /// <c>RequestedTheme</c>（所以本方法没有 theme 参数），失焦停算由框架自己管。
    /// </summary>
    public void Apply(Window w, BackdropKind kind)
    {
        Requested = kind;
        var (effective, degraded, reason) = BackdropPolicy.Resolve(kind, _systemAllows(), _acrylicOk(), _micaOk());

        w.SystemBackdrop = effective switch
        {
            BackdropKind.Acrylic => new Microsoft.UI.Xaml.Media.DesktopAcrylicBackdrop(),
            BackdropKind.Mica => new Microsoft.UI.Xaml.Media.MicaBackdrop(),
            _ => null,                                // 纯色：不挂 backdrop，由页面背景色承担
        };

        Effective = effective;
        Degraded = degraded;
        DegradedReason = reason;
    }

    /// <summary>
    /// 系统「透明效果」开关（个性化 → 颜色）。读的是 HKCU，本机当前 <c>EnableTransparency=0x1</c>；
    /// 读不到键（老系统/被策略删键）按**允许**处理——缺键不等于用户关了透明，误判会让默认亚克力凭空降级。
    /// </summary>
    public static bool TransparentEffectsAllowedBySystem()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("EnableTransparency") as int? != 0;
        }
        catch (Exception) { return true; }
    }
}
