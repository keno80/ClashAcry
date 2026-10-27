namespace ClashAcry.Core;

/// <summary>
/// 窗口背景材质的三档（spec §6/§8）。默认 <see cref="Acrylic"/>，且**不得为省开销自动降档**——
/// 只有系统或硬件真的不允许时才落 <see cref="Solid"/>，并且必须把原因讲出来。
/// </summary>
public enum BackdropKind
{
    Acrylic,
    Mica,
    Solid,
}
