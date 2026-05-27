namespace Dc.App.Services;

public interface IFilePicker
{
    string? PickOpenFile(string filter, string title);
    string? PickSaveFile(string filter, string defaultName, string title);
}
