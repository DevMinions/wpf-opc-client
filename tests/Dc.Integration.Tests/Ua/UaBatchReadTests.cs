using Dc.Opc.Abstractions;
using Dc.Opc.Ua;
using Dc.Integration.Tests.Ua.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace Dc.Integration.Tests.Ua;

[Collection("Ua")]
public class UaBatchReadTests
{
    private readonly EmbeddedUaServerFixture _fx;
    private readonly ITestOutputHelper _out;
    public UaBatchReadTests(EmbeddedUaServerFixture fx, ITestOutputHelper o) { _fx = fx; _out = o; }

    private async Task<OpcUaBrowser> ConnectAsync()
    {
        var b = new OpcUaBrowser();
        await b.ConnectAsync(new OpcConnectionOptions { ServerUri = _fx.AnonymousEndpointUrl });
        return b;
    }

    [Fact(Timeout = 30_000)]
    public async Task ReadValuesAsync_ReturnsValuePerNode_OrderedAndMatchesSingleRead()
    {
        await using var b = await ConnectAsync();
        var roots = await b.BrowseAsync(null);
        var demo = roots.First(n => n.DisplayName == "Demo");
        var children = await b.BrowseAsync(demo.Id);
        var ids = children.Where(n => n.Kind == OpcNodeKind.Item).Select(n => n.Id).ToList();
        Assert.NotEmpty(ids);

        var batch = await b.ReadValuesAsync(ids);
        Assert.Equal(ids.Count, batch.Count);
        for (var i = 0; i < ids.Count; i++)
        {
            var single = await b.ReadValueAsync(ids[i]);
            Assert.NotNull(batch[i]);
            Assert.Equal(single!.DataType, batch[i]!.DataType);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task ReadValuesAsync_EmptyInput_ReturnsEmpty()
    {
        await using var b = await ConnectAsync();
        var result = await b.ReadValuesAsync(new List<string>());
        Assert.Empty(result);
    }

    [Fact(Timeout = 120_000)]
    public async Task Benchmark_BatchRead_IsMuchFasterThanLoop_AndConsistent()
    {
        await using var host = new TestUaServerHost(TestUaServerHost.FindFreePort(), extraIntVars: 1000);
        await host.StartAsync();

        await using var b = new OpcUaBrowser();
        await b.ConnectAsync(new OpcConnectionOptions { ServerUri = host.EndpointUrl });

        var roots = await b.BrowseAsync(null);
        var demo = roots.First(n => n.DisplayName == "Demo");
        var children = await b.BrowseAsync(demo.Id);
        var ids = children.Where(n => n.Kind == OpcNodeKind.Item && n.DisplayName.StartsWith("Bench."))
                          .Select(n => n.Id).ToList();
        Assert.True(ids.Count >= 500, $"expected many bench nodes, got {ids.Count}");

        var swLoop = System.Diagnostics.Stopwatch.StartNew();
        var loop = new OpcNodeValue?[ids.Count];
        for (var i = 0; i < ids.Count; i++) loop[i] = await b.ReadValueAsync(ids[i]);
        swLoop.Stop();

        var swBatch = System.Diagnostics.Stopwatch.StartNew();
        var batch = await b.ReadValuesAsync(ids);
        swBatch.Stop();

        long loopMs = swLoop.ElapsedMilliseconds;
        long batchMs = swBatch.ElapsedMilliseconds;
        double speedup = (double)loopMs / Math.Max(1, batchMs);
        _out.WriteLine($"loop(1000)={loopMs}ms batch={batchMs}ms speedup={speedup:F1}x");

        for (var i = 0; i < ids.Count; i++)
            Assert.Equal(Convert.ToInt32(loop[i]!.Value), Convert.ToInt32(batch[i]!.Value));

        Assert.True(swBatch.ElapsedMilliseconds * 5 < swLoop.ElapsedMilliseconds,
            $"batch {swBatch.ElapsedMilliseconds}ms 应 < loop {swLoop.ElapsedMilliseconds}ms / 5");
    }
}
