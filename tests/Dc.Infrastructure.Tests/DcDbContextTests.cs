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

    [Fact]
    public async Task Formula_And_Inputs_Roundtrip()
    {
        var tag = new Tag { Id = "tag1", Item = "ns=2;s=T", DataType = 6, TaskId = "t1", GroupId = "g1", IsVirtual = false, ScaleFactor = 0.1, Offset = -5 };
        _db.Tags.Add(tag);

        var virt = new Tag { Id = "vtag1", Item = "补偿流量", DataType = 6, TaskId = "t1", GroupId = "g1", IsVirtual = true };
        _db.Tags.Add(virt);

        var f = new Formula { Id = "f1", Name = "补偿流量", Expression = "T * 1.8 + 32", OutputTagId = "vtag1", OutputUnit = "F", TaskId = "t1" };
        f.Inputs = new List<FormulaInput>
        {
            new() { Id = "fi1", FormulaId = "f1", Alias = "T", SourceTagId = "tag1" }
        };
        _db.Formulas.Add(f);
        await _db.SaveChangesAsync();

        await using var db2 = new DcDbContext(DcDbContextFactory.CreateOptions(_dbPath));
        var loaded = await db2.Formulas.Include(x => x.Inputs).SingleAsync(x => x.Id == "f1");
        Assert.Equal("T * 1.8 + 32", loaded.Expression);
        Assert.Equal("vtag1", loaded.OutputTagId);
        Assert.Single(loaded.Inputs);
        Assert.Equal("T", loaded.Inputs[0].Alias);
        Assert.Equal("tag1", loaded.Inputs[0].SourceTagId);

        var tagBack = await db2.Tags.SingleAsync(x => x.Id == "tag1");
        Assert.Equal(0.1, tagBack.ScaleFactor);
        Assert.Equal(-5, tagBack.Offset);
        Assert.False(tagBack.IsVirtual);

        var virtBack = await db2.Tags.SingleAsync(x => x.Id == "vtag1");
        Assert.True(virtBack.IsVirtual);
    }
}
