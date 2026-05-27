namespace Dc.App.Services;

public interface IBrowseDialog
{
    string? PickNodeId(string? initialServerUri = null);
}
