using Dc.Opc.Abstractions;

namespace Dc.Infrastructure.Orchestration;

public interface ITagValueTransform
{
    // 处理一个真实 Tag 的原始值。返回该批应发布/上抛的所有值：
    // 顺序为先缩放后真值，再触发的虚拟值（若有）。空集合表示该真值被丢弃。
    // 仅接收真实 Tag 的原始值（编排器保证虚拟值不回流进 Apply）。
    IReadOnlyList<TagValue> Apply(TagValue raw);

    // 热加真实 Tag 时调用（虚拟 Tag 不走此路径）。
    void OnTagsAdded(IEnumerable<TagDescriptor> tags);

    // 热删真实 Tag 时调用；若被某公式引用，该公式转 Failed 停止产出。
    void OnTagsRemoved(IEnumerable<TagDescriptor> tags);
}
