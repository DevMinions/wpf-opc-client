using CommunityToolkit.Mvvm.ComponentModel;
using Dc.Opc.Abstractions;

namespace Dc.App.ViewModels;

// 浏览结果行：包一个 OpcNode + 其当前值（批量读填充）。仅展示，零业务逻辑。
public partial class BrowseNodeRowViewModel : ObservableObject
{
    public OpcNode Node { get; }

    [ObservableProperty] private string _valueText = "";
    [ObservableProperty] private ushort _quality;
    [ObservableProperty] private bool _hasValue;

    public bool IsGood => Quality == 0xC0;

    public BrowseNodeRowViewModel(OpcNode node) => Node = node;

    // 批量读结果填入：null（文件夹/读失败）→ "—"、无值；否则取值文本（值为 null 也显 "—"）。
    public void SetValue(OpcNodeValue? v)
    {
        HasValue = v is not null;
        Quality = v?.Quality ?? 0;
        ValueText = v?.Value?.ToString() ?? "—";
    }

    partial void OnQualityChanged(ushort value) => OnPropertyChanged(nameof(IsGood));
}
