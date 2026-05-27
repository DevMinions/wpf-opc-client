using System.IO;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;

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
        Product = asm.GetCustomAttribute<AssemblyProductAttribute>()?.Product ?? "Dc.App";
        Version = $"v{asm.GetName().Version?.ToString(3) ?? "0.0.0"}";
        Company = asm.GetCustomAttribute<AssemblyCompanyAttribute>()?.Company ?? "Dc";
        Description = "通用 OPC 数据采集 — .NET 8 + WPF";
        Runtime = $".NET {Environment.Version}";

        try
        {
            var path = asm.Location;
            BuildDate = string.IsNullOrEmpty(path)
                ? "(未知)"
                : File.GetLastWriteTime(path).ToString("yyyy-MM-dd HH:mm");
        }
        catch
        {
            BuildDate = "(未知)";
        }
    }
}
