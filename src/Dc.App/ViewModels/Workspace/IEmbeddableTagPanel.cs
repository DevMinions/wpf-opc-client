namespace Dc.App.ViewModels.Workspace;

public interface IEmbeddableTagPanel
{
    bool IsEmbedded { get; set; }
    string? TaskScope { get; set; }
    Task LoadAsync();
    Task ImportAsync();
}
