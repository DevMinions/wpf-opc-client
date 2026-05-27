using Dc.Opc.Abstractions;
using Dc.Opc.Ae;
using Dc.Integration.Tests.Com.Fixtures;
using Xunit;

namespace Dc.Integration.Tests.Com;

[Collection("Com")]
public class AeBrowserSmokeTests
{
    private readonly DemoServerFixture _demo = new();

    // AE-2: 浏览根（areaId="") 应至少返回 1 个节点（Area 或 Source）
    [WindowsComFact("SampleCompany.AeSample", Timeout = 30_000)]
    public async Task AE2_BrowseRoot_NotEmpty()
    {
        var options = new OpcConnectionOptions
        {
            ServerUri = _demo.Host,
            ServerProgId = _demo.AeProgId
        };
        await using var browser = new OpcAeBrowser();
        await browser.ConnectAsync(options);

        var roots = await browser.BrowseAsync(null);
        Assert.NotEmpty(roots);
    }

    // AE-3: 在第一个 Area 下浏览，应至少有 1 个 Source 叶子（QualifiedName = SourceID）
    [WindowsComFact("SampleCompany.AeSample", Timeout = 30_000)]
    public async Task AE3_BrowseFirstArea_ContainsSource()
    {
        var options = new OpcConnectionOptions
        {
            ServerUri = _demo.Host,
            ServerProgId = _demo.AeProgId
        };
        await using var browser = new OpcAeBrowser();
        await browser.ConnectAsync(options);

        var roots = await browser.BrowseAsync(null);
        var firstArea = roots.FirstOrDefault(n => n.Kind == OpcNodeKind.Folder);
        if (firstArea is null)
        {
            // demo server 可能直接给扁平 Source 列表 — 通过
            Assert.Contains(roots, n => n.Kind == OpcNodeKind.Item);
            return;
        }

        var children = await browser.BrowseAsync(firstArea.Id);
        // 子级或者是再一级 Area 或者是 Source — 我们只要求非空
        Assert.NotEmpty(children);
    }
}
