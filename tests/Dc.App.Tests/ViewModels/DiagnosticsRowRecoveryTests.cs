using Dc.App.ViewModels;
using Dc.Infrastructure.Orchestration;
using Xunit;

namespace Dc.App.Tests.ViewModels;

public class DiagnosticsRowRecoveryTests
{
    private static TaskDiagnostics D(string id, int restart, ConnectionState s)
        => new(id, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0, 0, restart, 1, State: s);

    [Fact]
    public void Apply_RestartingToRunning_SetsJustRecovered()
    {
        var vm = new DiagnosticsRowViewModel();
        vm.Apply(D("t1", 0, ConnectionState.Running));
        vm.Apply(D("t1", 1, ConnectionState.Restarting));
        vm.Apply(D("t1", 1, ConnectionState.Running));   // 重启后恢复
        Assert.True(vm.JustRecovered);
        Assert.Equal(ConnectionState.Running, vm.State);
        vm.RecoveryTickForTest(); // 模拟倒计时到点
        Assert.False(vm.JustRecovered);
    }

    [Fact]
    public void Apply_FaultedToRunning_SetsJustRecovered()
    {
        var vm = new DiagnosticsRowViewModel();
        vm.Apply(D("t1", 2, ConnectionState.Faulted));
        vm.Apply(D("t1", 3, ConnectionState.Running));
        Assert.True(vm.JustRecovered);
    }

    [Fact]
    public void TickRecovery_CountsDownFullRecoveryTicks_ThenClears()
    {
        var vm = new DiagnosticsRowViewModel();
        vm.Apply(D("t1", 0, ConnectionState.Faulted));
        vm.Apply(D("t1", 1, ConnectionState.Running));  // 触发，ticks=5（RecoveryTicks）
        Assert.True(vm.JustRecovered);
        // 前 4 次 tick 仍亮
        for (var i = 0; i < 4; i++) { vm.TickRecovery(); Assert.True(vm.JustRecovered); }
        // 第 5 次到 0 清标
        vm.TickRecovery();
        Assert.False(vm.JustRecovered);
        // 再 tick 不下溢、保持 false
        vm.TickRecovery();
        Assert.False(vm.JustRecovered);
    }

    [Fact]
    public void Apply_RunningToRunning_DoesNotRecover()
    {
        var vm = new DiagnosticsRowViewModel();
        vm.Apply(D("t1", 0, ConnectionState.Running));
        vm.Apply(D("t1", 0, ConnectionState.Running));
        Assert.False(vm.JustRecovered);
    }
}
