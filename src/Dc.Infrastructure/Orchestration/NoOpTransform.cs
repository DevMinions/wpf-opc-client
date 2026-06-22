using Dc.Opc.Abstractions;

namespace Dc.Infrastructure.Orchestration;

// 无公式且无缩放时使用，零额外开销：直接返回单元素数组透传真值。
public sealed class NoOpTransform : ITagValueTransform
{
    public static readonly NoOpTransform Instance = new();
    private NoOpTransform() { }

    public IReadOnlyList<TagValue> Apply(TagValue raw) => new[] { raw };
    public void OnTagsAdded(IEnumerable<TagDescriptor> tags) { }
    public void OnTagsRemoved(IEnumerable<TagDescriptor> tags) { }
}
