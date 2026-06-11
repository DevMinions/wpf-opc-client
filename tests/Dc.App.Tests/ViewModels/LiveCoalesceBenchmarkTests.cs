using System.Diagnostics;
using Dc.App.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace Dc.App.Tests.ViewModels;

public class LiveCoalesceBenchmarkTests
{
    private readonly ITestOutputHelper _out;
    public LiveCoalesceBenchmarkTests(ITestOutputHelper o) => _out = o;

    [Fact]
    public void Coalesce_1000Keys_x10_OutputsDistinct_AndRatioAbout10()
    {
        // 1000 个 key 各 10 次更新 = 10000 条原始；轮转交错（贴近真实高频流）。
        var items = new List<(string Key, int Val)>(10_000);
        for (var round = 0; round < 10; round++)
            for (var k = 0; k < 1000; k++)
                items.Add(($"Stress::tag{k}", round * 1000 + k));
        var idx = 0;

        var c = new LiveValueCoalescer<int>();
        var appliedKeys = new HashSet<string>();

        var sw = Stopwatch.StartNew();
        c.Coalesce(
            () => idx < items.Count ? (true, items[idx].Key, items[idx++].Val) : (false, string.Empty, 0),
            (k, _, _) => appliedKeys.Add(k));
        sw.Stop();

        var ratio = (double)c.LastInputCount / Math.Max(1, c.LastOutputCount);
        _out.WriteLine($"input={c.LastInputCount} output={c.LastOutputCount} ratio={ratio:F1} elapsed={sw.ElapsedMilliseconds}ms");

        Assert.Equal(10_000, c.LastInputCount);
        Assert.Equal(1000, c.LastOutputCount);
        Assert.Equal(1000, appliedKeys.Count);
        Assert.True(ratio > 9.0, $"合并比应≈10，实测 {ratio:F1}");
        Assert.True(sw.ElapsedMilliseconds < 50, $"1 万条合并应 < 50ms，实测 {sw.ElapsedMilliseconds}ms");
    }
}
