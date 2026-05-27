using CommunityToolkit.Mvvm.ComponentModel;
using Dc.Domain.Entities;

namespace Dc.App.ViewModels;

public partial class ConfigEntryEditorViewModel : ObservableObject
{
    [ObservableProperty] private string _title;
    [ObservableProperty] private string _key = string.Empty;
    [ObservableProperty] private string _value = string.Empty;

    public string? OriginalId { get; }
    public bool KeyIsReadOnly => OriginalId is not null;

    public ConfigEntryEditorViewModel(ConfigEntry? existing)
    {
        if (existing is null)
        {
            _title = "新建配置";
        }
        else
        {
            _title = "编辑配置";
            OriginalId = existing.Id;
            _key = existing.Key;
            _value = existing.Value;
        }
    }

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(Key)) errors.Add("Key 不能为空");
        return errors;
    }

    public ConfigEntry ToEntity() => new()
    {
        Id = OriginalId ?? string.Empty,
        Key = Key.Trim(),
        Value = Value ?? string.Empty
    };
}
