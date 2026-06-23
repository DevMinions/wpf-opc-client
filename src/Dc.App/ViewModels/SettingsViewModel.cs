using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dc.App.Services;
using Dc.App.Services.I18n;
using Dc.App.Services.Theme;
using Dc.Domain.Entities;
using Dc.Infrastructure.Backup;
using Dc.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dc.App.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly IDbContextFactory<DcDbContext> _dbFactory;
    private readonly IConfigEditorDialog _editor;
    private readonly IConfigBackupService _backup;
    private readonly IFilePicker _filePicker;
    private readonly ILocalizer _loc;

    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private ConfigEntry? _selectedEntry;

    public ObservableCollection<ConfigEntry> Entries { get; } = new();
    public ThemeSettingsViewModel Theme { get; }
    public LanguageSettingsViewModel Language { get; }

    public SettingsViewModel(
        IDbContextFactory<DcDbContext> dbFactory,
        IConfigEditorDialog editor,
        IConfigBackupService backup,
        IFilePicker filePicker,
        IThemeService theme,
        ILanguageService language,
        ILocalizer localizer)
    {
        _dbFactory = dbFactory;
        _editor = editor;
        _backup = backup;
        _filePicker = filePicker;
        _loc = localizer;
        Title = _loc["Settings_PageTitle"];
        Theme = new ThemeSettingsViewModel(theme);
        Language = new LanguageSettingsViewModel(language);
        _ = LoadAsync();
    }

    [RelayCommand]
    private async Task ExportBackupAsync()
    {
        var path = _filePicker.PickSaveFile("JSON|*.json", $"dc-backup-{DateTime.Now:yyyyMMdd-HHmmss}.json", _loc["Settings_ExportPickerTitle"]);
        if (path is null) return;

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var bundle = await _backup.ExportAsync(db);
            var json = _backup.SerializeToJson(bundle);
            await File.WriteAllTextAsync(path, json);
            MessageDialog.Show(_loc["Tags_ExportSucceededTitle"],
                _loc.Format("Settings_ExportSucceededMessage", bundle.Tasks.Count, bundle.Tags.Count, bundle.Configs.Count, path),
                MessageDialogKind.Success);
        }
        catch (Exception ex)
        {
            MessageDialog.Show(_loc["Common_Error"], _loc.Format("Settings_ExportFailed", ex.Message), MessageDialogKind.Error);
        }
    }

    [RelayCommand]
    private async Task ImportBackupAsync()
    {
        var path = _filePicker.PickOpenFile("JSON|*.json", _loc["Settings_ImportPickerTitle"]);
        if (path is null) return;

        // 三选一(替换/合并/取消):自定义两按钮对话框无法表达,保留原生 MessageBox。
        var replace = System.Windows.MessageBox.Show(
            _loc["Settings_ImportModeMessage"],
            _loc["Settings_ImportModeTitle"], System.Windows.MessageBoxButton.YesNoCancel, System.Windows.MessageBoxImage.Question);

        if (replace == System.Windows.MessageBoxResult.Cancel) return;
        var mode = replace == System.Windows.MessageBoxResult.Yes
            ? BackupImportMode.Replace
            : BackupImportMode.Merge;

        try
        {
            var json = await File.ReadAllTextAsync(path);
            var bundle = _backup.DeserializeFromJson(json);
            await using var db = await _dbFactory.CreateDbContextAsync();
            var result = await _backup.ImportAsync(db, bundle, mode);

            var msg = _loc.Format("Settings_ImportResultMessage", result.TasksImported, result.TagsImported, result.ConfigsImported);
            if (result.Errors.Count > 0)
                msg += "\n" + _loc.Format("Tags_ImportErrorsHeader", result.Errors.Count) + "\n" + string.Join("\n", result.Errors.Take(5));
            MessageDialog.Show(_loc["Tags_ImportResultTitle"], msg, result.Errors.Count > 0 ? MessageDialogKind.Warning : MessageDialogKind.Success);
            await LoadAsync();
        }
        catch (Exception ex)
        {
            MessageDialog.Show(_loc["Common_Error"], _loc.Format("Settings_ImportFailed", ex.Message), MessageDialogKind.Error);
        }
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        if (IsLoading) return;
        IsLoading = true;
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var list = await db.Configs.AsNoTracking().OrderBy(c => c.Key).ToListAsync();
            Entries.Clear();
            foreach (var e in list) Entries.Add(e);
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task NewAsync()
    {
        var edited = _editor.Edit(null);
        if (edited is null) return;

        edited.Id = UlidGenerator.NewId();
        await using var db = await _dbFactory.CreateDbContextAsync();
        try
        {
            db.Configs.Add(edited);
            await db.SaveChangesAsync();
            Entries.Add(edited);
        }
        catch (DbUpdateException ex)
        {
            MessageDialog.Show(_loc["Common_Error"], _loc.Format("Settings_SaveFailedKeyExists", ex.InnerException?.Message ?? ex.Message), MessageDialogKind.Error);
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task EditAsync()
    {
        if (SelectedEntry is null) return;
        var edited = _editor.Edit(SelectedEntry);
        if (edited is null) return;

        await using var db = await _dbFactory.CreateDbContextAsync();
        var entity = await db.Configs.FirstOrDefaultAsync(c => c.Id == edited.Id);
        if (entity is null) return;
        entity.Value = edited.Value;
        await db.SaveChangesAsync();

        var idx = Entries.IndexOf(SelectedEntry);
        if (idx >= 0)
        {
            Entries[idx] = entity;
            SelectedEntry = entity; // 替换后重选，保持高亮一致
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task DeleteAsync()
    {
        var entry = SelectedEntry;
        if (entry is null) return;

        var confirm = MessageDialog.Confirm(_loc["Tags_DeleteConfirmTitle"], _loc.Format("Settings_DeleteConfirmMessage", entry.Key), MessageDialogKind.Warning);
        if (!confirm) return;

        await using var db = await _dbFactory.CreateDbContextAsync();
        await db.Configs.Where(c => c.Id == entry.Id).ExecuteDeleteAsync();
        Entries.Remove(entry);
    }

    private bool HasSelection() => SelectedEntry is not null;

    partial void OnSelectedEntryChanged(ConfigEntry? value)
    {
        DeleteCommand.NotifyCanExecuteChanged();
        EditCommand.NotifyCanExecuteChanged();
    }
}
