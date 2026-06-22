using Dc.Domain.Entities;

namespace Dc.App.ViewModels.Workspace;

public interface IEmbeddableTagPanel
{
    bool IsEmbedded { get; set; }
    string? TaskScope { get; set; }
    Group? GroupFilter { get; set; }
    Task LoadAsync();
    Task ImportAsync();

    /// <summary>
    /// 内嵌模式下,Tag 面板无分组时请求跳转到「分组」页签创建分组。
    /// 独立页不触发(无 Groups tab 可跳)。
    /// </summary>
    event Action? NavigateToGroupsRequested;
}
