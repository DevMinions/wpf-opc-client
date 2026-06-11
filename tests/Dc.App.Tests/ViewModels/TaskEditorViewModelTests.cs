using Dc.App.ViewModels;
using Dc.Opc.Abstractions;

namespace Dc.App.Tests.ViewModels;

public class TaskEditorViewModelTests
{
    private static TaskEditorViewModel New() => new(null, Array.Empty<IOpcBrowserFactory>());

    [Fact]
    public void Default_Protocol_Is_Ua()
        => Assert.Equal(OpcProtocol.Ua, New().Protocol);

    [Fact]
    public void Protocols_List_Starts_With_Ua()
        => Assert.Equal(OpcProtocol.Ua, New().Protocols[0]);

    [Fact]
    public void Fresh_Vm_Has_Errors_Because_Server_Empty_And_CannotSave()
    {
        var vm = New();   // Server 默认空
        Assert.True(vm.HasErrors);
        Assert.False(vm.CanSave);
    }

    [Fact]
    public void Valid_Input_Clears_Errors_And_CanSave()
    {
        var vm = New();
        vm.Server = "opc.tcp://localhost:4840";
        Assert.False(vm.HasErrors);
        Assert.True(vm.CanSave);
    }

    [Fact]
    public void Interval_Below_One_Is_Invalid()
    {
        var vm = New();
        vm.Server = "opc.tcp://x";   // 先清掉 server 错误
        vm.Interval = 0;
        Assert.True(vm.HasErrors);
        vm.Interval = 1000;
        Assert.False(vm.HasErrors);
    }

    [Fact]
    public void Deviation_OutOfRange_Is_Invalid()
    {
        var vm = New();
        vm.Server = "opc.tcp://x";
        vm.Deviation = 150;
        Assert.True(vm.HasErrors);
        vm.Deviation = 50;
        Assert.False(vm.HasErrors);
    }

    [Fact]
    public void TcpAddress_Without_Port_Is_Invalid()
    {
        var vm = New();
        vm.Server = "opc.tcp://x";
        vm.TcpAddress = "127.0.0.1";   // 缺 :port
        Assert.True(vm.HasErrors);
        vm.TcpAddress = "127.0.0.1:5000";
        Assert.False(vm.HasErrors);
    }
}
