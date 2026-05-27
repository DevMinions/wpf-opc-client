using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Dc.App.ViewModels.Dashboard;

namespace Dc.App.Views.Dashboard;

public partial class DashboardView : UserControl
{
    public DashboardView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is DashboardViewModel old) old.PropertyChanged -= OnVmPropertyChanged;
        if (e.NewValue is DashboardViewModel vm) vm.PropertyChanged += OnVmPropertyChanged;
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DashboardViewModel.HealthScore))
            Dispatcher.BeginInvoke(UpdateHealthArc);
    }

    private void UpdateHealthArc()
    {
        if (DataContext is not DashboardViewModel vm) return;

        // Arc parameters: radius=45, centered in 110x110 grid
        const double radius = 45;
        const double cx = 55, cy = 55;
        const double startAngle = -90; // top
        var endAngle = startAngle + 360.0 * (vm.HealthScore / 100.0);

        var startRad = startAngle * Math.PI / 180;
        var endRad = endAngle * Math.PI / 180;

        var startX = cx + radius * Math.Cos(startRad);
        var startY = cy + radius * Math.Sin(startRad);
        var endX = cx + radius * Math.Cos(endRad);
        var endY = cy + radius * Math.Sin(endRad);

        var isLarge = vm.HealthScore > 50;

        var figure = new PathFigure { StartPoint = new Point(startX, startY) };
        figure.Segments.Add(new ArcSegment
        {
            Point = new Point(endX, endY),
            Size = new Size(radius, radius),
            IsLargeArc = isLarge,
            SweepDirection = SweepDirection.Clockwise
        });

        var geometry = new PathGeometry { Figures = { figure } };
        HealthArc.Data = geometry;
    }
}
