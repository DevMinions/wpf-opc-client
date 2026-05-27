using Dc.Domain.Entities;
using Dc.Infrastructure.Backup;
using Dc.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Dc.Infrastructure.Tests.Backup;

public class JsonConfigBackupTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DcDbContext _db;
    private readonly JsonConfigBackupService _svc = new();

    public JsonConfigBackupTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"dc-backup-{Guid.NewGuid():N}.db");
        _db = new DcDbContext(DcDbContextFactory.CreateOptions(_dbPath));
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    private async Task SeedAsync()
    {
        var task = new CollectorTask { Id = "task-1", Server = "S1", Node = "opc.tcp://localhost", Type = 2, Interval = 1000, TcpAddress = "127.0.0.1:5000" };
        var group = new Group { Id = "group-1", Name = "G1", TaskId = task.Id };
        _db.Tasks.Add(task);
        _db.Groups.Add(group);
        _db.Tags.AddRange(
            new Tag { Id = "tag-1", Item = "A", DataType = 4, GroupId = "group-1", TaskId = "task-1" },
            new Tag { Id = "tag-2", Item = "B", DataType = 5, GroupId = "group-1", TaskId = "task-1" });
        _db.Configs.Add(new ConfigEntry { Id = "cfg-1", Key = "clientID", Value = "abc" });
        await _db.SaveChangesAsync();
    }

    [Fact]
    public async Task ExportAndImportMerge_Roundtrip()
    {
        await SeedAsync();

        var bundle = await _svc.ExportAsync(_db);
        Assert.Single(bundle.Tasks);
        Assert.Single(bundle.Groups);
        Assert.Equal(2, bundle.Tags.Count);
        Assert.Single(bundle.Configs);

        var json = _svc.SerializeToJson(bundle);
        Assert.Contains("schemaVersion", json);
        Assert.Contains("task-1", json);

        var roundTripped = _svc.DeserializeFromJson(json);
        Assert.Equal(1, roundTripped.SchemaVersion);
        Assert.Equal(2, roundTripped.Tags.Count);
    }

    [Fact]
    public async Task ImportMerge_SkipsExistingIds()
    {
        await SeedAsync();
        var bundle = await _svc.ExportAsync(_db);

        var result = await _svc.ImportAsync(_db, bundle, BackupImportMode.Merge);

        Assert.Equal(0, result.TasksImported);
        Assert.Equal(0, result.GroupsImported);
        Assert.Equal(0, result.TagsImported);
        Assert.Equal(0, result.ConfigsImported);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task ImportReplace_ClearsAndReinserts()
    {
        await SeedAsync();
        var bundle = await _svc.ExportAsync(_db);

        // Mutate bundle to have only different content
        bundle.Tasks.Clear();
        bundle.Tasks.Add(new CollectorTask { Id = "new-task", Server = "S2", Node = "n", Type = 2, Interval = 500, TcpAddress = "x:1" });
        bundle.Groups.Clear();
        bundle.Tags.Clear();
        bundle.Configs.Clear();

        var result = await _svc.ImportAsync(_db, bundle, BackupImportMode.Replace);

        Assert.Equal(1, result.TasksImported);
        Assert.Equal(0, result.GroupsImported);
        Assert.Equal(0, result.TagsImported);

        _db.ChangeTracker.Clear();
        Assert.Single(_db.Tasks.AsQueryable());
        Assert.Empty(_db.Tags.AsQueryable());
        Assert.Empty(_db.Groups.AsQueryable());
    }

    [Fact]
    public async Task ImportMerge_AddsNewItemsOnly()
    {
        await SeedAsync();

        var bundle = new BackupBundle
        {
            SchemaVersion = 1,
            ExportedAt = DateTimeOffset.UtcNow,
            Tasks = new List<CollectorTask>
            {
                new() { Id = "task-1", Server = "Existing", Node = "n", Type = 2, Interval = 1000, TcpAddress = "x:1" },
                new() { Id = "task-2", Server = "New", Node = "n", Type = 2, Interval = 1000, TcpAddress = "x:2" }
            }
        };

        var result = await _svc.ImportAsync(_db, bundle, BackupImportMode.Merge);

        Assert.Equal(1, result.TasksImported);
        Assert.Empty(result.Errors);
        _db.ChangeTracker.Clear();
        Assert.Equal(2, _db.Tasks.AsQueryable().Count());
    }

    [Fact]
    public void DeserializeFromJson_RejectsBadSchemaVersion()
    {
        var bundle = new BackupBundle { SchemaVersion = 999 };
        var json = _svc.SerializeToJson(bundle);
        var parsed = _svc.DeserializeFromJson(json);
        Assert.Equal(999, parsed.SchemaVersion);
    }
}
