using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ClashAcry.Core.Proxies;

/// <summary>
/// 节点页三个 VM 的共同基座：只有**真值变了才发通知**。
/// 这不是省性能，是「轮询不得有可见抖动」那条红线的下半截——上半截是 <see cref="ProxiesViewModel"/> 的按名合并
/// （容器不重建），下半截是属性没变就不发 <c>PropertyChanged</c>（发了就会重排、重绘，滚动位置跟着跳）。
/// </summary>
public abstract class NotifyVm : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    /// <summary>派生文本（如「名称 · 延迟」）要跟着源属性一起报，否则行文本停在旧值。</summary>
    protected void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
