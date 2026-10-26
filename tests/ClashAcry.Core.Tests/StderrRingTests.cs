namespace ClashAcry.Core.Tests;

public class StderrRingTests
{
    [Fact]
    public void Ring_KeepsOnlyLastCapacityLines_AndPreservesOrder()
    {
        var ring = new StderrRing(3);
        foreach (var l in new[] { "a", "b", "c", "d", "e" }) ring.Add(l);
        Assert.Equal(new[] { "c", "d", "e" }, ring.Snapshot());
    }

    [Fact]
    public void Ring_SurvivesConcurrentWriters_AndSnapshotIsStableCopy()
    {
        // 裁定 R-8d：两条输出流由 .NET 在不同线程池回调上派发，轮询线程同时在读。
        // 裸 List 的结局是丢行或 "Collection was modified" 直接把状态机掀翻。
        var ring = new StderrRing(50);
        Exception? boom = null;
        using var stopReading = new CancellationTokenSource();

        var w1 = new Thread(() => { try { for (var i = 0; i < 500; i++) ring.Add($"w1-{i}"); } catch (Exception e) { boom = e; } });
        var w2 = new Thread(() => { try { for (var i = 0; i < 500; i++) ring.Add($"w2-{i}"); } catch (Exception e) { boom = e; } });
        var reader = new Thread(() =>
        {
            try { while (!stopReading.IsCancellationRequested) _ = ring.Snapshot(); }
            catch (Exception e) { boom = e; }
        });

        w1.Start(); w2.Start(); reader.Start();
        w1.Join(); w2.Join();
        stopReading.Cancel(); reader.Join();

        Assert.Null(boom);
        var snap = ring.Snapshot();
        Assert.Equal(50, snap.Count);                                        // 容量守住了
        Assert.All(snap, l => Assert.Matches(@"^w\d-\d+$", l));              // 1000 次并发写没吐出半行或外来内容
        var copy = ring.Snapshot();
        ring.Clear();
        Assert.Equal(50, copy.Count);                                        // Snapshot 是拷贝，不是活视图
    }
}
