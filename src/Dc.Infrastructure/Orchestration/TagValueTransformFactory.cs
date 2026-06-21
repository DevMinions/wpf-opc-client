namespace Dc.Infrastructure.Orchestration;

public sealed class TagValueTransformFactory : ITagValueTransformFactory
{
    public ITagValueTransform Create(string taskId, TransformConfig config)
    {
        bool hasScale = config.ScaleByTagId.Values
            .Any(s => s.ScaleFactor is not null || s.Offset is not null);
        bool hasFormula = config.Formulas.Count > 0;

        if (!hasScale && !hasFormula)
            return NoOpTransform.Instance;

        return new TagValueTransform(config);
    }
}
