using Dc.Domain.Entities;

namespace Dc.App.Services;

public interface IConfigEditorDialog
{
    ConfigEntry? Edit(ConfigEntry? existing);
}
