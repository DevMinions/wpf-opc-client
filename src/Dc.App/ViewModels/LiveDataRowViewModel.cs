using CommunityToolkit.Mvvm.ComponentModel;
using Dc.Opc.Abstractions;

namespace Dc.App.ViewModels;

public partial class LiveDataRowViewModel : ObservableObject
{
    [ObservableProperty] private string _taskId = string.Empty;
    [ObservableProperty] private string _item = string.Empty;
    [ObservableProperty] private object? _value;
    [ObservableProperty] private ushort _quality;
    [ObservableProperty] private DateTimeOffset _timestamp;
    [ObservableProperty] private bool _isGood;
    [ObservableProperty] private bool _isUncertain;
    [ObservableProperty] private int _updateCount;

    public void Apply(TagValue v)
    {
        if (!Equals(Value, v.Value)) Value = v.Value;
        if (Quality != v.Quality) Quality = v.Quality;
        if (Timestamp != v.Timestamp) Timestamp = v.Timestamp;
        if (IsGood != v.IsGood) IsGood = v.IsGood;
        if (IsUncertain != v.IsUncertain) IsUncertain = v.IsUncertain; // 三态：Good/Uncertain/Bad
        UpdateCount += 1;
    }
}
