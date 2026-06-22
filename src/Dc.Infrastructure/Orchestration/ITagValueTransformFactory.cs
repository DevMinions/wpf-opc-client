namespace Dc.Infrastructure.Orchestration;

public interface ITagValueTransformFactory
{
    // 无公式且无缩放时返回 NoOpTransform.Instance 以零开销。
    ITagValueTransform Create(string taskId, TransformConfig config);
}
