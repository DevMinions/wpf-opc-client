using Dc.App.ViewModels;
using Dc.Infrastructure.Orchestration;
using Xunit;

namespace Dc.App.Tests.ViewModels;

public class DiagnosticsRowRecoveryTests
{
    private static TaskDiagnostics D(string id, int restart, ConnectionState s)
        => new(id, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0, 0, restart, 1, State: s);

    [Fact]
    public void Apply_RestartingToRunning_WithRestartIncrease_SetsJustRecovered()
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
    public void Apply_FaultedToRunning_WithRestartIncrease_SetsJustRecovered()
    {
        var vm = new DiagnosticsRowViewModel();
        vm.Apply(D("t1", 2, ConnectionState.Faulted));
        vm.Apply(D("t1", 3, ConnectionState.Running));
        Assert.True(vm.JustRecovered);
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
