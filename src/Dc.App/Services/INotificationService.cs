namespace Dc.App.Services;

public interface INotificationService
{
    /// <summary>Show a non-modal error notification (toast).</summary>
    void ShowError(string title, string message);
}
