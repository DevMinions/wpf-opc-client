using Dc.Domain.Entities;

namespace Dc.App.Services;

public interface ITagEditorDialog
{
    TagEditResult? Edit(
        IEnumerable<Group> availableGroups,
        Tag? existing,
        Group? defaultGroup = null,
        Func<string, CollectorTask?>? taskLookup = null,
        IReadOnlyCollection<Tag>? taskTags = null,
        IReadOnlyCollection<Formula>? existingFormulas = null);
}
