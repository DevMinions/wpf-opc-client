using Dc.Opc.Abstractions;
using Dc.Opc.Da;
using Dc.Integration.Tests.Com.Fixtures;
using Xunit;

namespace Dc.Integration.Tests.Com;

[Collection("Com")]
public class DaBrowserSmokeTests
{
    private readonly DemoServerFixture _demo = new();

    // DA-2: 扫描本机能列出 demo server URL
    [WindowsComFact("SampleCompany.DaSample", Timeout = 30_000)]
    public async Task DA2_EnumerateServersLocalhost_ContainsDemo()
    {
        await using var browser = new OpcDaBrowser();
        var urls = await browser.EnumerateServersAsync("localhost");

        Assert.NotEmpty(urls);
        Assert.Contains(urls, u => u.Contains(_demo.DaProgId, StringComparison.OrdinalIgnoreCase));
    }

    // DA-3: 连上 demo server 后浏览根，应至少包含 "SimulatedData" 文件夹
    [WindowsComFact("SampleCompany.DaSample", Timeout = 30_000)]
    public async Task DA3_BrowseRoot_ContainsSimulatedData()
    {
        var options = new OpcConnectionOptions
        {
            ServerUri = _demo.Host,
            ServerProgId = _demo.DaProgId
        };
        await using var browser = new OpcDaBrowser();
        await browser.ConnectAsync(options);

        var children = await browser.BrowseAsync(parentNodeId: null);

        Assert.NotEmpty(children);
        Assert.Contains(children, n =>
            n.Kind == OpcNodeKind.Folder &&
            n.DisplayName.Equals("SimulatedData", StringComparison.OrdinalIgnoreCase));
    }

    // DA-4: ServerClsid 给值时 vendor 拼 opcda://host/progId/{clsid}，
    //        connect 应跳过 OPCEnum 解析，直接 CoCreateInstance 成功。
    [WindowsComFact("SampleCompany.DaSample", Timeout = 30_000)]
    public async Task DA4_ClsidFallback_ConnectsWithoutOpcEnumLookup()
    {
        var options = new OpcConnectionOptions
        {
            ServerUri = _demo.Host,
            ServerProgId = _demo.DaProgId,
            ServerClsid = _demo.DaClsid // 已带 {} 形式
        };
        await using var browser = new OpcDaBrowser();
        await browser.ConnectAsync(options);

        var children = await browser.BrowseAsync(null);
        Assert.NotEmpty(children);
    }
}
