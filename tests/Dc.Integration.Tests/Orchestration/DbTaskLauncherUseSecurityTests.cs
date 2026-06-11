using Dc.Domain.Entities;
using Dc.Infrastructure.Orchestration;
using Xunit;

namespace Dc.Integration.Tests.Orchestration;

public class DbTaskLauncherUseSecurityTests
{
    private static CollectorTask Task(bool useSecurity) => new()
    {
        Id = "t1", Server = "s", Node = "opc.tcp://x:1/", Type = 2,
        Interval = 1000, Deviation = 0, TcpAddress = "127.0.0.1:5000", UseSecurity = useSecurity
    };

    [Fact]
    public void ToStartRequest_MapsUseSecurity_True()
        => Assert.True(DbTaskLauncher.ToStartRequest(Task(true)).OpcOptions.UseSecurity);

    [Fact]
    public void ToStartRequest_MapsUseSecurity_False()
        => Assert.False(DbTaskLauncher.ToStartRequest(Task(false)).OpcOptions.UseSecurity);
}
