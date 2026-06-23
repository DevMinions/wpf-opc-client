using System.Diagnostics;
using Dc.Opc.Abstractions;
using Dc.Opc.Da;
using Dc.Integration.Tests.Com.Fixtures;
using Xunit;

namespace Dc.Integration.Tests.Com.Resilience;

[Collection("Com")]
public class DaResilienceTests
{
    private readonly DemoServerFixture _demo = new();

    // RES-1: 订阅运行中杀掉 demo server 进程 → 等几秒（COM SCM 会按需重启 LocalServer32）→
    //        在重启后我们重新订阅应该能再次收到值。本测试不依赖 orchestrator，直接验证：
    //        断开后重新 ConnectAsync + SubscribeAsync 仍能工作。
    [WindowsComFact("SampleCompany.DaSample", Timeout = 60_000)]
    public async Task RES1_KillDemoServer_RestartedClientCanReceiveAgain()
    {
        // 第一阶段：建立订阅，收到 1 条
        var options = new OpcConnectionOptions
        {
            ServerUri = _demo.Host,
            ServerProgId = _demo.DaProgId,
            SamplingInterval = TimeSpan.FromMilliseconds(500),
            HeartbeatInterval = TimeSpan.FromSeconds(5)
        };

        await using (var sub1 = new OpcDaSubscriber("res-1a", options))
        {
            await sub1.ConnectAsync();
            await sub1.SubscribeAsync(new[] { new TagDescriptor("t", "SimulatedData.Ramp", 0) });
            using var cts1 = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await sub1.TagValues.ReadAsync(cts1.Token); // 收到一条就行
        }

        // 杀进程
        foreach (var p in Process.GetProcessesByName("OpcDaAeServer"))
        {
            try { p.Kill(true); p.WaitForExit(5000); } catch { }
            p.Dispose();
        }

        // 等系统冷却 + SCM 释放
        await Task.Delay(TimeSpan.FromSeconds(3));

        // 第二阶段：新订阅器应能 connect 上（SCM 重启 LocalServer32），并再次收到值
        await using var sub2 = new OpcDaSubscriber("res-1b", options);
        await sub2.ConnectAsync();
        await sub2.SubscribeAsync(new[] { new TagDescriptor("t", "SimulatedData.Ramp", 0) });

        using var cts2 = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var v = await sub2.TagValues.ReadAsync(cts2.Token);
        Assert.Equal("SimulatedData.Ramp", v.Item);
    }

    // RES-2: 扫描不存在的主机 192.0.2.1（RFC5737 文档保留地址，永远不通），
    //        EnumerateServersAsync 必须在 30s 内抛或返回空，不挂死。
    [WindowsComFact("SampleCompany.DaSample", Timeout = 60_000)]
    public async Task RES2_ScanUnreachableHost_TimesOutGracefully()
    {
        await using var browser = new OpcDaBrowser();

        using var hardCts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var urls = await browser.EnumerateServersAsync("192.0.2.1", hardCts.Token);
            // 返空也算 OK — 重点是不挂
            sw.Stop();
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(40), $"扫描应在 40s 内完成，实际 {sw.Elapsed}");
        }
        catch (Exception)
        {
            // vendor 抛 OpcResultException / COMException 也算 OK
            sw.Stop();
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(40), $"扫描应在 40s 内抛，实际 {sw.Elapsed}");
        }
    }
}
