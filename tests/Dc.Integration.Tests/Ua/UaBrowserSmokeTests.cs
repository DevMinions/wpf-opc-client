using Dc.Opc.Abstractions;
using Dc.Opc.Ua;
using Dc.Integration.Tests.Ua.Fixtures;
using Xunit;

namespace Dc.Integration.Tests.Ua;

[Collection("Ua")]
public class UaBrowserSmokeTests
{
    private readonly EmbeddedUaServerFixture _ua;
    public UaBrowserSmokeTests(EmbeddedUaServerFixture ua) => _ua = ua;

    // UA-2: 浏览根（null parent）实际从 ObjectsFolder 出发，应返回非空节点列表
    // OpcUaBrowser.BrowseAsync(null) 硬编码起点为 ObjectIds.ObjectsFolder，
    // 因此返回的是 ObjectsFolder 的子节点（Server、Demo 等），而不是根节点本身。
    [Fact(Timeout = 15_000)]
    public async Task UA2_BrowseRoot_ReturnsNonEmptyList()
    {
        var options = new OpcConnectionOptions { ServerUri = _ua.AnonymousEndpointUrl };
        await using var browser = new OpcUaBrowser();
        await browser.ConnectAsync(options);

        var children = await browser.BrowseAsync(parentNodeId: null);

        Assert.NotEmpty(children);
    }

    // UA-3: 下钻 ObjectsFolder（null parent）应能找到 MinimalUaNodeManager 暴露的 Demo 文件夹
    [Fact(Timeout = 15_000)]
    public async Task UA3_BrowseObjectsFolder_ContainsDemoFolder()
    {
        var options = new OpcConnectionOptions { ServerUri = _ua.AnonymousEndpointUrl };
        await using var browser = new OpcUaBrowser();
        await browser.ConnectAsync(options);

        var children = await browser.BrowseAsync(parentNodeId: null);

        Assert.Contains(children, n => n.DisplayName == "Demo");
    }

    // UA-4: 读取 Demo.Int32 当前值 → 数据类型 Int32、值为 int、质量 Good（0xC0）
    [Fact(Timeout = 15_000)]
    public async Task UA4_ReadValue_ReturnsTypedValueAndGoodQuality()
    {
        var options = new OpcConnectionOptions { ServerUri = _ua.AnonymousEndpointUrl };
        await using var browser = new OpcUaBrowser();
        await browser.ConnectAsync(options);

        var r = await browser.ReadValueAsync("ns=2;s=Demo.Int32");

        Assert.NotNull(r);
        Assert.Equal("Int32", r!.DataType);
        Assert.IsType<int>(r.Value);
        Assert.Equal(0xC0, r.Quality);
    }
}
