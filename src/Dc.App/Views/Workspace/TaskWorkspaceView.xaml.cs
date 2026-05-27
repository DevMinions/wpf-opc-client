using System.Windows.Controls;
using Dc.App.ViewModels.Workspace;
using Serilog;

namespace Dc.App.Views.Workspace;

public partial class TaskWorkspaceView : UserControl
{
    public TaskWorkspaceView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (DataContext is TaskWorkspaceViewModel vm)
            {
                vm.Start(Dispatcher);
                _ = vm.LoadAsync();
            }
        };
        Unloaded += (_, _) =>
        {
            if (DataContext is TaskWorkspaceViewModel vm) vm.Stop();
        };
    }

    private TaskWorkspaceViewModel? Vm => DataContext as TaskWorkspaceViewModel;

    // 状态筛选 / tab 现由 XAML TwoWay 绑定 StatusFilter / SelectedTab 驱动（不再用 Checked 事件）。

    private async void OnStart(object s, System.Windows.RoutedEventArgs e)
    {
        try { if (Vm is { } v) await v.StartSelectedAsync(); }
        catch (Exception ex) { Log.Error(ex, "StartSelected failed"); }
    }

    private async void OnStop(object s, System.Windows.RoutedEventArgs e)
    {
        try { if (Vm is { } v) await v.StopSelectedAsync(); }
        catch (Exception ex) { Log.Error(ex, "StopSelected failed"); }
    }

    private async void OnRestart(object s, System.Windows.RoutedEventArgs e)
    {
        try { if (Vm is { } v) await v.RestartSelectedAsync(); }
        catch (Exception ex) { Log.Error(ex, "RestartSelected failed"); }
    }

    private async void OnNewTask(object s, System.Windows.RoutedEventArgs e)
    {
        try { if (Vm is { } v) await v.NewTaskAsync(); }
        catch (Exception ex) { Log.Error(ex, "NewTask failed"); }
    }

    private async void OnImport(object s, System.Windows.RoutedEventArgs e)
    {
        try { if (Vm is { } v) await v.ImportAsync(); }
        catch (Exception ex) { Log.Error(ex, "Import failed"); }
    }
}
