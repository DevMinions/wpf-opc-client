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
}
