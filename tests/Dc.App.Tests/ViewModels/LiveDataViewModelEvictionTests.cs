using System.Windows.Threading;
using Dc.App.ViewModels;
using Dc.Infrastructure.Messaging;
using Dc.Infrastructure.Orchestration;
using Dc.Opc.Abstractions;
using Xunit;

namespace Dc.App.Tests.ViewModels;

public class LiveDataViewModelEvictionTests
{
    private sealed class FakePublisherFactory : IPublisherFactory
    {
        public IPublisher Create(string address) => throw new NotSupportedException();
    }

    private static TaskOrchestrator Orch()
        => new(Array.Empty<IOpcSubscriberFactory>(), new FakePublisherFactory(), new OrchestratorOptions(), null);

    private static LiveDataViewModel NewVm(out TaskOrchestrator orch)
    {
        orch = Orch();
        return new LiveDataViewModel(orch, Dispatcher.CurrentDispatcher);
    }

    [Fact]
    public void Flush_BeyondMaxRows_EvictsOldest_AndRowCountStable()
    {
        var vm = NewVm(out _);
        for (var i = 0; i < 5050; i++)
            vm.EnqueueForTest("T1", new TagValue($"item{i}", i, 0xC0, DateTimeOffset.UtcNow));
        vm.FlushForTest();

        Assert.Equal(5000, vm.Rows.Count);
        Assert.DoesNotContain(vm.Rows, r => r.Item == "item0");
        Assert.DoesNotContain(vm.Rows, r => r.Item == "item49");
        Assert.Contains(vm.Rows, r => r.Item == "item50");
        Assert.Contains(vm.Rows, r => r.Item == "item5049");
    }

    [Fact]
    public void Flush_ExactlyMaxRows_NoEviction()
    {
        var vm = NewVm(out _);
        for (var i = 0; i < 5000; i++)
            vm.EnqueueForTest("T1", new TagValue($"item{i}", i, 0xC0, DateTimeOffset.UtcNow));
        vm.FlushForTest();
        Assert.Equal(5000, vm.Rows.Count);
        Assert.Contains(vm.Rows, r => r.Item == "item0");      // 边界：恰好不淘汰
        Assert.Contains(vm.Rows, r => r.Item == "item4999");
    }

    [Fact]
    public void Flush_MultipleAccumulating_EvictsAcrossFlushes()
    {
        var vm = NewVm(out _);
        for (var i = 0; i < 4900; i++)
            vm.EnqueueForTest("T1", new TagValue($"item{i}", i, 0xC0, DateTimeOffset.UtcNow));
        vm.FlushForTest();
        Assert.Equal(4900, vm.Rows.Count); // 未超限

        for (var i = 4900; i < 5100; i++) // 再加 200 个新 key → 共 5100，淘汰 100
            vm.EnqueueForTest("T1", new TagValue($"item{i}", i, 0xC0, DateTimeOffset.UtcNow));
        vm.FlushForTest();
        Assert.Equal(5000, vm.Rows.Count);
        Assert.DoesNotContain(vm.Rows, r => r.Item == "item0");   // 第一批最旧的被淘汰
        Assert.DoesNotContain(vm.Rows, r => r.Item == "item99");
        Assert.Contains(vm.Rows, r => r.Item == "item100");
        Assert.Contains(vm.Rows, r => r.Item == "item5099");
    }

    [Fact]
    public void SearchText_RapidChanges_RefreshesOnceAfterDebounceTick()
    {
        var vm = NewVm(out _);
        vm.ResetRefreshCountForTest();

        vm.SearchText = "a";
        vm.SearchText = "ab";
        vm.SearchText = "abc";
        // 防抖窗口内多次赋值，Tick 未到 → 尚未刷新
        Assert.Equal(0, vm.RefreshCountForTest);

        vm.DebounceTickForTest(); // 模拟 250ms 静默后 Tick
        Assert.Equal(1, vm.RefreshCountForTest);
    }

    [Fact]
    public void TaskFilter_Change_RefreshesImmediately()
    {
        var vm = NewVm(out _);
        vm.ResetRefreshCountForTest();
        vm.TaskFilter = "T1";
        Assert.Equal(1, vm.RefreshCountForTest); // 下拉即时刷新
    }

    [Fact]
    public void FlushStats_AfterFlushes_ReportsRatioAndRows()
    {
        var vm = NewVm(out _);
        // 同 key 多次 + 多 key：制造合并比（5 轮 × 100 key = 500 输入 → 100 输出）
        for (var r = 0; r < 5; r++)
            for (var k = 0; k < 100; k++)
                vm.EnqueueForTest("T1", new TagValue($"item{k}", r, 0xC0, DateTimeOffset.UtcNow));
        vm.FlushForTest();

        var s = vm.GetFlushStats();
        Assert.Equal(100, s.Rows);
        Assert.True(s.CoalesceRatio > 4.5, $"500 输入/100 输出≈5，实测 {s.CoalesceRatio:F1}");
        Assert.True(s.P50Ms >= 0);
        Assert.True(s.P95Ms >= s.P50Ms);
    }
}
