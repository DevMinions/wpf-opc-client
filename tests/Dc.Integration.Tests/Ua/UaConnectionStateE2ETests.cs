using Dc.Opc.Abstractions;
using Dc.Opc.Ua;
using Dc.Infrastructure.Messaging;
using Dc.Infrastructure.Orchestration;
using Dc.Integration.Tests.Ua.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace Dc.Integration.Tests.Ua;

// 真 OpcUaSubscriber 经编排器 + 真 server 停启，确定性验证连接状态徽章状态机。
// 时序敏感：server-down 须撑过 HeartbeatTimeout（subscriber 自身 SessionReconnectHandler
// 先于看门狗处理瞬断），看门狗重连失败才进 Faulted；server 回来后看门狗重连成功 + 心跳回流 → Running。
[Collection("Timing-Sensitive")]
public class UaConnectionStateE2ETests
{
    private readonly ITestOutputHelper _out;
    public UaConnectionStateE2ETests(ITestOutputHelper o) => _out = o;

    private sealed class NoopPublisher : IPublisher
    {
        public Task PublishAsync<T>(T message, CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NoopPublisherFactory : IPublisherFactory
    {
        public IPublisher Create(string address) => new NoopPublisher();
    }

    private static async Task WaitForState(TaskOrchestrator o, string id, ConnectionState want, int ms)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            if (o.GetDiagnostics().Any(x => x.TaskId == id && x.State == want)) return;
            await Task.Delay(50);
        }
        var cur = o.GetDiagnostics().FirstOrDefault(x => x.TaskId == id)?.State.ToString() ?? "缺失";
        Assert.True(o.GetDiagnostics().Any(x => x.TaskId == id && x.State == want),
            $"{id} 未在 {ms}ms 内到达 {want}（当前 {cur}）");
    }

    [Fact(Timeout = 120_000)]
    public async Task RealUaTask_ServerDownThenUp_TransitionsRunningFaultedRunning()
    {
        var port = TestUaServerHost.FindFreePort();
        var host = new TestUaServerHost(port);
        await host.StartAsync();

        await using var orch = new TaskOrchestrator(
            new IOpcSubscriberFactory[] { new OpcUaSubscriberFactory() },
            new NoopPublisherFactory(),
            new OrchestratorOptions
            {
                WatchdogInterval = TimeSpan.FromMilliseconds(500),
                HeartbeatTimeout = TimeSpan.FromSeconds(2),
                FaultThreshold = 2,
                StopDrainTimeout = TimeSpan.FromSeconds(1),
            },
            null);

        var req = new TaskStartRequest(
            TaskId: "ua-state",
            Protocol: OpcProtocol.Ua,
            OpcOptions: new OpcConnectionOptions
            {
                ServerUri = host.EndpointUrl,
                UseSecurity = false, // 测试 server 仅暴露 SecurityPolicy=None
                SamplingInterval = TimeSpan.FromMilliseconds(200),
                HeartbeatInterval = TimeSpan.FromMilliseconds(500),
                KeepAliveInterval = TimeSpan.FromMilliseconds(500),
                ReconnectPeriod = TimeSpan.FromMilliseconds(500),
            },
            PublisherAddress: "noop",
            Tags: new[] { new TagDescriptor("d", "ns=2;s=Demo.Int32", 0) });

        await orch.StartAsync(req);
        await WaitForState(orch, "ua-state", ConnectionState.Running, 15_000);
        _out.WriteLine("→ Running");

        host.Stop();
        await WaitForState(orch, "ua-state", ConnectionState.Faulted, 30_000);
        Assert.Contains(orch.GetDiagnostics(), x => x.TaskId == "ua-state");
        _out.WriteLine("→ Faulted（server 停）");

        await host.StartAsync();
        await WaitForState(orch, "ua-state", ConnectionState.Running, 30_000);
        var restarts = orch.GetDiagnostics().Single(x => x.TaskId == "ua-state").RestartCount;
        _out.WriteLine($"→ Running 恢复，RestartCount={restarts}");
        Assert.True(restarts >= 1, "应至少重启一次");

        await host.DisposeAsync();
    }
}
