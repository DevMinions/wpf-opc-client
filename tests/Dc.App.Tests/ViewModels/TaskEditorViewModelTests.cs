using Dc.App.ViewModels;
using Dc.Domain.Entities;
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

    [Fact]
    public void NewTask_UseSecurity_DefaultsTrue()
        => Assert.True(new TaskEditorViewModel().UseSecurity);

    [Fact]
    public void Edit_RoundTripsUseSecurity_AndToEntity()
    {
        var existing = new CollectorTask { Id = "a", Server = "s", Node = "n", Type = 2, UseSecurity = false,
            Interval = 1000, TcpAddress = "1.2.3.4:5" };
        var vm = new TaskEditorViewModel(existing, Array.Empty<IOpcBrowserFactory>());
        Assert.False(vm.UseSecurity);
        Assert.False(vm.ToEntity().UseSecurity);
    }

    [Fact]
    public void IsUaProtocol_TracksProtocol()
    {
        var vm = new TaskEditorViewModel();
        Assert.True(vm.IsUaProtocol);
        vm.Protocol = OpcProtocol.Da;
        Assert.False(vm.IsUaProtocol);
    }

    [Fact]
    public void Ua_SettingNode_MirrorsToServer()
    {
        var vm = new TaskEditorViewModel();          // 默认 Ua
        vm.Node = "opc.tcp://host:4840/x";
        Assert.Equal("opc.tcp://host:4840/x", vm.Server);
        Assert.Equal("opc.tcp://host:4840/x", vm.ToEntity().Server);
    }

    [Fact]
    public void Da_SettingNode_DoesNotMirrorToServer()
    {
        var vm = new TaskEditorViewModel { Protocol = OpcProtocol.Da };
        vm.Server = "Matrikon.OPC.Simulation.1";
        vm.Node = "192.168.1.10";
        Assert.Equal("Matrikon.OPC.Simulation.1", vm.Server);   // DA 不镜像
    }

    [Fact]
    public void ServerLabel_And_Placeholder_TrackProtocol()
    {
        var vm = new TaskEditorViewModel();          // Ua
        Assert.Equal("服务器:", vm.ServerLabel);
        Assert.Contains("opc.tcp", vm.ServerPlaceholder);
        vm.Protocol = OpcProtocol.Da;
        Assert.Equal("ProgID:", vm.ServerLabel);
        Assert.DoesNotContain("opc.tcp", vm.ServerPlaceholder);
    }
}
