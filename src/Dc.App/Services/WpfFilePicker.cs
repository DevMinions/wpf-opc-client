using Microsoft.Win32;

namespace Dc.App.Services;

public sealed class WpfFilePicker : IFilePicker
{
    public string? PickOpenFile(string filter, string title)
    {
        var dlg = new OpenFileDialog { Filter = filter, Title = title };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    public string? PickSaveFile(string filter, string defaultName, string title)
    {
        var dlg = new SaveFileDialog { Filter = filter, FileName = defaultName, Title = title };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }
}
