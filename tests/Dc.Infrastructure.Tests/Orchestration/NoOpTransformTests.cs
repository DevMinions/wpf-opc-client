using Dc.Infrastructure.Orchestration;
using Dc.Opc.Abstractions;
using Xunit;

namespace Dc.Infrastructure.Tests.Orchestration;

public class NoOpTransformTests
{
    [Fact]
    public void Apply_ReturnsSingleElement_Unchanged()
    {
        var t = NoOpTransform.Instance;
        var v = new TagValue("A", 42.0, 0xC0, DateTimeOffset.UtcNow);
        var outp = t.Apply(v);
        Assert.Single(outp);
        Assert.Equal(v, outp[0]);
    }
}
