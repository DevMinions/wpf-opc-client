namespace Dc.App.Services;

public interface IConfirmDialog
{
    /// <summary>Show a yes/no confirmation. Returns true if the user confirms.</summary>
    bool Confirm(string title, string message);
}
