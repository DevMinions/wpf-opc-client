using Dc.Domain.Entities;

namespace Dc.App.Services;

public interface ITaskEditorDialog
{
    CollectorTask? Edit(CollectorTask? existing);
}
