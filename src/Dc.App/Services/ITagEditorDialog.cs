using Dc.Domain.Entities;

namespace Dc.App.Services;

public interface ITagEditorDialog
{
    TagEditResult? Edit(
        string taskId,
        Tag? existing,
        Func<string, CollectorTask?>? taskLookup = null,
        IReadOnlyCollection<Tag>? taskTags = null,
        IReadOnlyCollection<Formula>? existingFormulas = null);
}
