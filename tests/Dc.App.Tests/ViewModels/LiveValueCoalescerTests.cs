using Dc.App.ViewModels;
using Xunit;

namespace Dc.App.Tests.ViewModels;

public class LiveValueCoalescerTests
{
    // 用 Queue 模拟 ConcurrentQueue 的 TryDequeue。
    private static Func<(bool, string, T)> DequeueFrom<T>(Queue<(string Key, T Val)> q)
        => () => q.Count > 0 ? (true, q.Peek().Key, q.Dequeue().Val) : (false, string.Empty, default!);

    [Fact]
    public void Coalesce_MultiKey_KeepsLatestPerKey_InFirstSeenOrder()
    {
        var q = new Queue<(string, int)>(new[]
        {
            ("a", 1), ("b", 2), ("a", 3), ("c", 4), ("b", 5),
        });
        var c = new LiveValueCoalescer<int>();
        var applied = new List<(string Key, int Val)>();

        c.Coalesce(DequeueFrom(q), (k, v) => applied.Add((k, v)));

        Assert.Equal(new[] { ("a", 3), ("b", 5), ("c", 4) }, applied);
        Assert.Equal(5, c.LastInputCount);
        Assert.Equal(3, c.LastOutputCount);
    }

    [Fact]
    public void Coalesce_SingleKeyHighFrequency_AppliesOnceWithLastValue()
    {
        var q = new Queue<(string, int)>(Enumerable.Range(1, 10_000).Select(i => ("k", i)));
        var c = new LiveValueCoalescer<int>();
        var applied = new List<(string, int)>();

        c.Coalesce(DequeueFrom(q), (k, v) => applied.Add((k, v)));

        Assert.Single(applied);
        Assert.Equal(("k", 10_000), applied[0]);
        Assert.Equal(10_000, c.LastInputCount);
        Assert.Equal(1, c.LastOutputCount);
    }

    [Fact]
    public void Coalesce_EmptyInput_AppliesNothing()
    {
        var q = new Queue<(string, int)>();
        var c = new LiveValueCoalescer<int>();
        var applied = 0;

        c.Coalesce(DequeueFrom(q), (_, _) => applied++);

        Assert.Equal(0, applied);
        Assert.Equal(0, c.LastInputCount);
        Assert.Equal(0, c.LastOutputCount);
    }

    [Fact]
    public void Coalesce_ReusedInstance_ResetsBetweenCalls()
    {
        var c = new LiveValueCoalescer<int>();
        var q1 = new Queue<(string, int)>(new[] { ("a", 1) });
        c.Coalesce(DequeueFrom(q1), (_, _) => { });

        var q2 = new Queue<(string, int)>(new[] { ("b", 2), ("b", 3) });
        var applied = new List<(string, int)>();
        c.Coalesce(DequeueFrom(q2), (k, v) => applied.Add((k, v)));

        Assert.Equal(new[] { ("b", 3) }, applied);
        Assert.Equal(2, c.LastInputCount);
        Assert.Equal(1, c.LastOutputCount);
    }
}
