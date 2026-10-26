namespace ClashAcry.Core;

/// 历史名叫 Stderr，实为「内核输出环」：真 mihomo 的 Go 日志走 stdout（Task 1 实测），两流都进这里。
public sealed class StderrRing(int capacity)
{
    private readonly object _gate = new();
    private readonly Queue<string> _q = new();

    public void Add(string line)
    {
        lock (_gate) { _q.Enqueue(line); if (_q.Count > capacity) _q.Dequeue(); }
    }

    public void Clear() { lock (_gate) _q.Clear(); }

    public IReadOnlyList<string> Snapshot() { lock (_gate) return _q.ToArray(); }   // 拷贝，不是活视图
}
