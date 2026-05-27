using Dc.Domain.Entities;
using Dc.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Dc.Infrastructure.Tests;

public class DcDbContextTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DcDbContext _db;

    public DcDbContextTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"dc-test-{Guid.NewGuid():N}.db");
        _db = new DcDbContext(DcDbContextFactory.CreateOptions(_dbPath));
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    [Fact]
    public void EnsureCreated_GeneratesExpectedTables()
    {
        using var conn = _db.Database.GetDbConnection();
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name";
        using var rd = cmd.ExecuteReader();
        var tables = new List<string>();
        while (rd.Read()) tables.Add(rd.GetString(0));

        Assert.Contains("dc_tags", tables);
        Assert.Contains("dc_groups", tables);
        Assert.Contains("dc_tasks", tables);
        Assert.Contains("dc_configs", tables);
    }

    [Fact]
    public void Columns_UseSnakeCase()
    {
        using var conn = _db.Database.GetDbConnection();
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(dc_tags)";
        using var rd = cmd.ExecuteReader();
        var cols = new List<string>();
        while (rd.Read()) cols.Add(rd.GetString(1));

        Assert.Contains("id", cols);
        Assert.Contains("item", cols);
        Assert.Contains("data_type", cols);
        Assert.Contains("task_id", cols);
        Assert.Contains("group_id", cols);
        Assert.Contains("created_at", cols);
        Assert.Contains("updated_at", cols);
    }

    [Fact]
    public async Task Tag_RoundTrip_PreservesAllFields()
    {
        var tag = new Tag
        {
            Id = UlidGenerator.NewId(),
            Item = "Random.Int1", DataType = 2,
            TaskId = "task-1", GroupId = "group-1"
        };
        _db.Tags.Add(tag);
        await _db.SaveChangesAsync();

        var fetched = await _db.Tags.FirstOrDefaultAsync(
            t => t.TaskId == "task-1" && t.GroupId == "group-1" && t.Item == "Random.Int1");

        Assert.NotNull(fetched);
        Assert.Equal(2, fetched!.DataType);
        Assert.True(fetched.CreatedAt != default);
        Assert.True(fetched.UpdatedAt != default);
    }

    [Fact]
    public async Task Tag_CompositeUniqueIndex_RejectsDuplicates()
    {
        _db.Tags.Add(new Tag { Id = UlidGenerator.NewId(), Item = "A.B", DataType = 1, TaskId = "t", GroupId = "g" });
        await _db.SaveChangesAsync();

        _db.Tags.Add(new Tag { Id = UlidGenerator.NewId(), Item = "A.B", DataType = 1, TaskId = "t", GroupId = "g" });

        await Assert.ThrowsAsync<DbUpdateException>(() => _db.SaveChangesAsync());
    }

    [Fact]
    public async Task Task_WithGroupsAndTags_LoadsViaInclude()
    {
        var task = new CollectorTask
        {
            Id = UlidGenerator.NewId(),
            Server = "Matrikon.OPC.Simulation",
            Node = "localhost",
            Type = 1,
            Interval = 1000,
            TcpAddress = "127.0.0.1:5000"
        };
        var group = new Group { Id = UlidGenerator.NewId(), Name = "g1", TaskId = task.Id };
        var tags = new[]
        {
            new Tag { Id = UlidGenerator.NewId(), Item = "A", DataType = 1, TaskId = task.Id, GroupId = group.Id },
            new Tag { Id = UlidGenerator.NewId(), Item = "B", DataType = 1, TaskId = task.Id, GroupId = group.Id }
        };
        _db.Tasks.Add(task);
        _db.Groups.Add(group);
        _db.Tags.AddRange(tags);
        await _db.SaveChangesAsync();

        _db.ChangeTracker.Clear();
        var loaded = await _db.Tasks
            .Include(t => t.Groups).ThenInclude(g => g.Tags)
            .ToListAsync();

        var loadedTask = Assert.Single(loaded);
        var loadedGroup = Assert.Single(loadedTask.Groups);
        Assert.Equal(2, loadedGroup.Tags.Count);
    }

    [Fact]
    public async Task Config_LookupByKey()
    {
        _db.Configs.Add(new ConfigEntry { Id = UlidGenerator.NewId(), Key = "clientID", Value = "abc-123" });
        await _db.SaveChangesAsync();

        var fetched = await _db.Configs.FirstOrDefaultAsync(c => c.Key == "clientID");

        Assert.NotNull(fetched);
        Assert.Equal("abc-123", fetched!.Value);
    }

    [Fact]
    public async Task Config_KeyUniqueIndex_RejectsDuplicates()
    {
        _db.Configs.Add(new ConfigEntry { Id = UlidGenerator.NewId(), Key = "k", Value = "v1" });
        await _db.SaveChangesAsync();
        _db.Configs.Add(new ConfigEntry { Id = UlidGenerator.NewId(), Key = "k", Value = "v2" });

        await Assert.ThrowsAsync<DbUpdateException>(() => _db.SaveChangesAsync());
    }
}
