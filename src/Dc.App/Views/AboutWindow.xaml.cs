using System.Windows;
using Dc.App.ViewModels;

namespace Dc.App.Views;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        DataContext = new AboutViewModel();
    }
}
