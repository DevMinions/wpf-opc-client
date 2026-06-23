using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dc.App.Services;
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

    [ObservableProperty] private string _title = "系统配置";
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private ConfigEntry? _selectedEntry;

    public ObservableCollection<ConfigEntry> Entries { get; } = new();
    public ThemeSettingsViewModel Theme { get; }

    public SettingsViewModel(
        IDbContextFactory<DcDbContext> dbFactory,
        IConfigEditorDialog editor,
        IConfigBackupService backup,
        IFilePicker filePicker,
        IThemeService theme)
    {
        _dbFactory = dbFactory;
        _editor = editor;
        _backup = backup;
        _filePicker = filePicker;
        Theme = new ThemeSettingsViewModel(theme);
        _ = LoadAsync();
    }

    [RelayCommand]
    private async Task ExportBackupAsync()
    {
        var path = _filePicker.PickSaveFile("JSON|*.json", $"dc-backup-{DateTime.Now:yyyyMMdd-HHmmss}.json", "导出全部配置");
        if (path is null) return;

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var bundle = await _backup.ExportAsync(db);
            var json = _backup.SerializeToJson(bundle);
            await File.WriteAllTextAsync(path, json);
            MessageDialog.Show("导出成功",
                $"已导出 {bundle.Tasks.Count} 任务 / {bundle.Tags.Count} Tag / {bundle.Configs.Count} 配置到 {path}",
                MessageDialogKind.Success);
        }
        catch (Exception ex)
        {
            MessageDialog.Show("错误", $"导出失败：{ex.Message}", MessageDialogKind.Error);
        }
    }

    [RelayCommand]
    private async Task ImportBackupAsync()
    {
        var path = _filePicker.PickOpenFile("JSON|*.json", "导入备份");
        if (path is null) return;

        // 三选一(替换/合并/取消):自定义两按钮对话框无法表达,保留原生 MessageBox。
        var replace = System.Windows.MessageBox.Show(
            "是 = 替换模式（清空现有数据再导入）\n否 = 合并模式（保留现有数据，仅插入新 ID）\n取消 = 取消导入",
            "选择导入模式", System.Windows.MessageBoxButton.YesNoCancel, System.Windows.MessageBoxImage.Question);

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

            var msg = $"已导入 任务 {result.TasksImported} / Tag {result.TagsImported} / 配置 {result.ConfigsImported}";
            if (result.Errors.Count > 0)
                msg += $"\n错误 ({result.Errors.Count}):\n" + string.Join("\n", result.Errors.Take(5));
            MessageDialog.Show("导入结果", msg, result.Errors.Count > 0 ? MessageDialogKind.Warning : MessageDialogKind.Success);
            await LoadAsync();
        }
        catch (Exception ex)
        {
            MessageDialog.Show("错误", $"导入失败：{ex.Message}", MessageDialogKind.Error);
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
            MessageDialog.Show("错误", $"保存失败（Key 可能已存在）：{ex.InnerException?.Message ?? ex.Message}", MessageDialogKind.Error);
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

        var confirm = MessageDialog.Confirm("删除确认", $"确定删除配置项 {entry.Key}？", MessageDialogKind.Warning);
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
