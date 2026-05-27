using Dc.Domain.Entities;

namespace Dc.App.Services;

public interface ITagEditorDialog
{
    Tag? Edit(
        IEnumerable<Group> availableGroups,
        Tag? existing,
        Group? defaultGroup = null,
        Func<string, CollectorTask?>? taskLookup = null);
}
