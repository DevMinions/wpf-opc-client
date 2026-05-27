using System.ComponentModel;
using Dc.Domain.Entities;

namespace Dc.App.ViewModels.Workspace;

public interface IEmbeddableGroupPanel : INotifyPropertyChanged
{
    bool IsEmbedded { get; set; }
    CollectorTask? TaskFilter { get; set; }
    Group? SelectedGroup { get; }
    Task LoadAsync();
}
