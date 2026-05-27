using Dc.Domain.Entities;

namespace Dc.App.Services;

public interface IGroupEditorDialog
{
    Group? Edit(IEnumerable<CollectorTask> availableTasks, Group? existing, CollectorTask? defaultTask = null);
}
