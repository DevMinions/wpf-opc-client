using Dc.Infrastructure.Persistence;

namespace Dc.Infrastructure.Backup;

public interface IConfigBackupService
{
    Task<BackupBundle> ExportAsync(DcDbContext db, CancellationToken ct = default);
    Task<BackupImportResult> ImportAsync(DcDbContext db, BackupBundle bundle, BackupImportMode mode, CancellationToken ct = default);
    string SerializeToJson(BackupBundle bundle);
    BackupBundle DeserializeFromJson(string json);
}
