using System.ComponentModel;
using Dc.Domain.Entities;

namespace Dc.App.ViewModels.Workspace;

public interface IEmbeddableGroupPanel : INotifyPropertyChanged
{
    bool IsEmbedded { get; set; }
    CollectorTask? TaskFilter { get; set; }
    Group? SelectedGroup { get; }
    Task LoadAsync();

    /// <summary>
    /// 内嵌模式下,分组面板无任务时请求跳到任务列表(新建任务)。
    /// 独立页不触发(无宿主可跳)。
    /// </summary>
    event Action? NavigateToTasksRequested;
}
