using System.IO;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using Dc.App.Services.I18n;

namespace Dc.App.ViewModels;

public sealed class AboutViewModel : ObservableObject
{
    public string Product { get; }
    public string Version { get; }
    public string Company { get; }
    public string Description { get; }
    public string Runtime { get; }
    public string BuildDate { get; }

    public AboutViewModel()
    {
        var asm = typeof(AboutViewModel).Assembly;
        Product = LocalizationManager.Instance["Tray_Tooltip"];
        Version = $"v{asm.GetName().Version?.ToString(3) ?? "0.0.0"}";
        Company = asm.GetCustomAttribute<AssemblyCompanyAttribute>()?.Company ?? "Dc";
        Description = LocalizationManager.Instance["About_Description"];
        Runtime = $".NET {Environment.Version}";

        try
        {
            var path = asm.Location;
            BuildDate = string.IsNullOrEmpty(path)
                ? LocalizationManager.Instance["About_Unknown"]
                : File.GetLastWriteTime(path).ToString("yyyy-MM-dd HH:mm");
        }
        catch
        {
            BuildDate = LocalizationManager.Instance["About_Unknown"];
        }
    }
}
