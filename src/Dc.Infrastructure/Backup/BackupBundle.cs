using Dc.Domain.Entities;

namespace Dc.Infrastructure.Backup;

public sealed class BackupBundle
{
    public int SchemaVersion { get; set; } = 1;
    public DateTimeOffset ExportedAt { get; set; }
    public string AppVersion { get; set; } = string.Empty;
    public List<CollectorTask> Tasks { get; set; } = new();
    public List<Group> Groups { get; set; } = new();
    public List<Tag> Tags { get; set; } = new();
    public List<ConfigEntry> Configs { get; set; } = new();
}

public sealed record BackupImportResult(
    int TasksImported, int GroupsImported, int TagsImported, int ConfigsImported,
    IReadOnlyList<string> Errors);

public enum BackupImportMode { Merge, Replace }
