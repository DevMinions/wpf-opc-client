using Dc.App.Composition;
using Dc.Infrastructure.Orchestration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Dc.App.Tests.Composition;

// 诊断 HTTP 端点（/healthz /readyz /metrics）在桌面端的装配契约：
// 默认关闭（不监听端口），appsettings "Diagnostics:Http" 可开，且作为 IHostedService 随宿主启停。
public class ServiceRegistrationDiagnosticsHttpTests
{
    private static ServiceProvider Build(Dictionary<string, string?>? settings = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(settings ?? new Dictionary<string, string?>())
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddDcApp(":memory:");
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Default_HttpEndpoint_Disabled()
    {
        // DiagnosticsReporter 仅实现 IAsyncDisposable，容器须异步释放
        await using var sp = Build();

        var opts = sp.GetRequiredService<MetricsServerOptions>();

        Assert.False(opts.Enabled);
    }

    [Fact]
    public async Task Default_Prefix_IsLocalhostOnly()
    {
        // 桌面端非管理员绑 "+" 需要 URL ACL 且暴露所有网卡；默认必须只绑 localhost。
        await using var sp = Build();

        var opts = sp.GetRequiredService<MetricsServerOptions>();

        Assert.StartsWith("http://localhost:", opts.Prefix);
    }

    [Fact]
    public async Task Configured_Enabled_OptionsApplied_And_RegisteredAsHostedService()
    {
        await using var sp = Build(new Dictionary<string, string?>
        {
            ["Diagnostics:Http:Enabled"] = "true",
            ["Diagnostics:Http:Prefix"] = "http://localhost:19191/"
        });

        var opts = sp.GetRequiredService<MetricsServerOptions>();

        Assert.True(opts.Enabled);
        Assert.Equal("http://localhost:19191/", opts.Prefix);
        Assert.Contains(sp.GetServices<IHostedService>(), s => s is MetricsHttpServer);
    }
}
