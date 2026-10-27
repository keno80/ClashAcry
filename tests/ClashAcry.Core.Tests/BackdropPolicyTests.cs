using ClashAcry.Core;

namespace ClashAcry.Core.Tests;

/// <summary>
/// backdrop 策略的纯函数面（spec §8：默认亚克力，降级必须明说）。
/// 三个开关的交叉判据只有「生效档 + 是否降级 + 文案」——材质本身要真窗口才看得见，归 UI.Tests 与 Task 16。
/// </summary>
public class BackdropPolicyTests
{
    [Theory]
    [InlineData(BackdropKind.Acrylic, true, true, true, BackdropKind.Acrylic, false)]   // 默认档：整窗亚克力
    [InlineData(BackdropKind.Mica, true, true, true, BackdropKind.Mica, false)]
    [InlineData(BackdropKind.Solid, true, true, true, BackdropKind.Solid, false)]
    [InlineData(BackdropKind.Acrylic, false, true, true, BackdropKind.Solid, true)]     // 系统关了透明
    [InlineData(BackdropKind.Mica, false, true, true, BackdropKind.Solid, true)]
    [InlineData(BackdropKind.Solid, false, true, true, BackdropKind.Solid, false)]      // 纯色本来就无所谓
    [InlineData(BackdropKind.Acrylic, true, false, true, BackdropKind.Solid, true)]     // 设备不支持亚克力
    [InlineData(BackdropKind.Mica, true, true, false, BackdropKind.Solid, true)]        // 设备不支持 Mica
    public void Resolve_PicksRequestedOrSolid(BackdropKind requested, bool allows, bool acrylicOk, bool micaOk,
                                             BackdropKind expected, bool expectDegraded)
    {
        var (effective, degraded, reason) = BackdropPolicy.Resolve(requested, allows, acrylicOk, micaOk);

        Assert.Equal(expected, effective);
        Assert.Equal(expectDegraded, degraded);
        // 降级必须给出可读原因：设置页要原样展示「当前实际生效：纯色（…）」
        if (expectDegraded) Assert.False(string.IsNullOrWhiteSpace(reason));
        else Assert.Equal("", reason);
    }

    [Fact]
    public void Resolve_NeverSilentlyDowngrades_DefaultAcrylic()
    {
        // spec §8 的用户拍板：默认值不得为省开销而降档；只有系统/硬件不允许时才降，且必须留话
        var (effective, degraded, _) = BackdropPolicy.Resolve(BackdropKind.Acrylic, true, true, true);
        Assert.Equal(BackdropKind.Acrylic, effective);
        Assert.False(degraded);
    }
}
