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
}
