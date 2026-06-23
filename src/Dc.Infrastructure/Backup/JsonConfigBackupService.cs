using System.Reflection;
using System.Text.Json;
using Dc.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dc.Infrastructure.Backup;

public sealed class JsonConfigBackupService : IConfigBackupService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task<BackupBundle> ExportAsync(DcDbContext db, CancellationToken ct = default)
    {
        return new BackupBundle
        {
            SchemaVersion = 1,
            ExportedAt = DateTimeOffset.UtcNow,
            AppVersion = typeof(JsonConfigBackupService).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
            Tasks = await db.Tasks.AsNoTracking().OrderBy(t => t.CreatedAt).ToListAsync(ct),
            Tags = await db.Tags.AsNoTracking().OrderBy(t => t.CreatedAt).ToListAsync(ct),
            Configs = await db.Configs.AsNoTracking().OrderBy(c => c.Key).ToListAsync(ct)
        };
    }

    public async Task<BackupImportResult> ImportAsync(DcDbContext db, BackupBundle bundle, BackupImportMode mode, CancellationToken ct = default)
    {
        if (bundle.SchemaVersion != 1)
            return new BackupImportResult(0, 0, 0, new[] { $"不支持的 schemaVersion: {bundle.SchemaVersion}" });

        var errors = new List<string>();

        if (mode == BackupImportMode.Replace)
        {
            await db.Tags.ExecuteDeleteAsync(ct);
            await db.Tasks.ExecuteDeleteAsync(ct);
            await db.Configs.ExecuteDeleteAsync(ct);
        }

        var (existingTaskIds, existingTagIds, existingConfigIds) = (
            new HashSet<string>(await db.Tasks.AsNoTracking().Select(t => t.Id).ToListAsync(ct)),
            new HashSet<string>(await db.Tags.AsNoTracking().Select(t => t.Id).ToListAsync(ct)),
            new HashSet<string>(await db.Configs.AsNoTracking().Select(c => c.Id).ToListAsync(ct)));

        var tasksToAdd = bundle.Tasks.Where(t => !existingTaskIds.Contains(t.Id)).ToList();
        var tagsToAdd = bundle.Tags.Where(t => !existingTagIds.Contains(t.Id)).ToList();
        var configsToAdd = bundle.Configs.Where(c => !existingConfigIds.Contains(c.Id)).ToList();

        db.Tasks.AddRange(tasksToAdd);
        db.Tags.AddRange(tagsToAdd);
        db.Configs.AddRange(configsToAdd);

        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) { errors.Add(ex.InnerException?.Message ?? ex.Message); }

        return new BackupImportResult(
            tasksToAdd.Count, tagsToAdd.Count, configsToAdd.Count, errors);
    }

    public string SerializeToJson(BackupBundle bundle) =>
        JsonSerializer.Serialize(bundle, JsonOptions);

    public BackupBundle DeserializeFromJson(string json) =>
        JsonSerializer.Deserialize<BackupBundle>(json, JsonOptions)
        ?? throw new InvalidDataException("无法解析 JSON 备份内容");
}
