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
        Assert.Contains("dc_tasks", tables);
        Assert.Contains("dc_configs", tables);
        Assert.DoesNotContain("dc_groups", tables); // 分组层已去除
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
        Assert.Contains("created_at", cols);
        Assert.Contains("updated_at", cols);
        Assert.DoesNotContain("group_id", cols); // 分组层已去除
    }

    [Fact]
    public async Task Tag_RoundTrip_PreservesAllFields()
    {
        var tag = new Tag
        {
            Id = UlidGenerator.NewId(),
            Item = "Random.Int1", DataType = 2,
            TaskId = "task-1"
        };
        _db.Tags.Add(tag);
        await _db.SaveChangesAsync();

        var fetched = await _db.Tags.FirstOrDefaultAsync(
            t => t.TaskId == "task-1" && t.Item == "Random.Int1");

        Assert.NotNull(fetched);
        Assert.Equal(2, fetched!.DataType);
        Assert.True(fetched.CreatedAt != default);
        Assert.True(fetched.UpdatedAt != default);
    }

    [Fact]
    public async Task Tag_CompositeUniqueIndex_RejectsDuplicates()
    {
        // 唯一索引现为 (item, task_id):同任务同 Item 拒绝。
        _db.Tags.Add(new Tag { Id = UlidGenerator.NewId(), Item = "A.B", DataType = 1, TaskId = "t" });
        await _db.SaveChangesAsync();

        _db.Tags.Add(new Tag { Id = UlidGenerator.NewId(), Item = "A.B", DataType = 1, TaskId = "t" });

        await Assert.ThrowsAsync<DbUpdateException>(() => _db.SaveChangesAsync());
    }

    [Fact]
    public async Task Task_WithTags_LoadsViaInclude()
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
        var tags = new[]
        {
            new Tag { Id = UlidGenerator.NewId(), Item = "A", DataType = 1, TaskId = task.Id },
            new Tag { Id = UlidGenerator.NewId(), Item = "B", DataType = 1, TaskId = task.Id }
        };
        _db.Tasks.Add(task);
        _db.Tags.AddRange(tags);
        await _db.SaveChangesAsync();

        _db.ChangeTracker.Clear();
        var loaded = await _db.Tasks.Include(t => t.Tags).ToListAsync();

        var loadedTask = Assert.Single(loaded);
        Assert.Equal(2, loadedTask.Tags.Count);
    }

    // 旧库一次性迁移:dc_tags.group_id + dc_groups 去除,Tag 直接挂任务。
    [Fact]
    public void MigrateDropGroupLayer_OldDb_DropsGroupsAndColumn_BackfillsTaskId()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dc-mig-{Guid.NewGuid():N}.db");
        try
        {
            // 1. 造旧 schema:dc_tags 带 group_id、udx_name 含 group_id、dc_groups 表存在。
            using (var conn = new SqliteConnection($"Data Source={path}"))
            {
                conn.Open();
                void Exec(string sql) { using var c = conn.CreateCommand(); c.CommandText = sql; c.ExecuteNonQuery(); }
                Exec("PRAGMA foreign_keys=OFF"); // 旧库 EF 连接 FK=false:允许造孤儿/空 task_id 行模拟历史数据
                Exec("CREATE TABLE dc_tasks (id TEXT PRIMARY KEY, created_at TEXT, updated_at TEXT, server TEXT, node TEXT, type INTEGER, interval INTEGER, deviation INTEGER, tcp_address TEXT)");
                Exec("CREATE TABLE dc_groups (id TEXT PRIMARY KEY, created_at TEXT, updated_at TEXT, name TEXT, task_id TEXT)");
                // group_id 带外键(镜像 EF 旧 schema):SQLite 不能 DROP 被外键引用的列 → 必须整表重建,本测试守护该路径。
                Exec("CREATE TABLE dc_tags (id TEXT PRIMARY KEY, created_at TEXT, updated_at TEXT, item TEXT, data_type INTEGER, task_id TEXT, group_id TEXT NOT NULL, " +
                     "CONSTRAINT FK_dc_tags_dc_groups_group_id FOREIGN KEY (group_id) REFERENCES dc_groups (id), " +
                     "CONSTRAINT FK_dc_tags_dc_tasks_task_id FOREIGN KEY (task_id) REFERENCES dc_tasks (id))");
                Exec("CREATE UNIQUE INDEX udx_name ON dc_tags (item, task_id, group_id)");
                Exec("INSERT INTO dc_groups VALUES ('g1','2020-01-01 00:00:00','2020-01-01 00:00:00','G','task-1')");
                Exec("INSERT INTO dc_tags VALUES ('tag1','2020-01-01 00:00:00','2020-01-01 00:00:00','A',1,'task-1','g1')");
                // 个别旧行 task_id 空 → 迁移应从分组回填
                Exec("INSERT INTO dc_tags VALUES ('tag2','2020-01-01 00:00:00','2020-01-01 00:00:00','B',1,'','g1')");
            }

            // 2. 跑迁移
            using (var db = new DcDbContext(DcDbContextFactory.CreateOptions(path)))
                DbSchemaInitializer.EnsureCreated(db);

            // 3. dc_groups 表 + group_id 列均已删
            using (var conn = new SqliteConnection($"Data Source={path}"))
            {
                conn.Open();
                long Scalar(string sql) { using var c = conn.CreateCommand(); c.CommandText = sql; return Convert.ToInt64(c.ExecuteScalar()); }
                Assert.Equal(0, Scalar("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='dc_groups'"));
                Assert.Equal(0, Scalar("SELECT COUNT(*) FROM pragma_table_info('dc_tags') WHERE name='group_id'"));
            }

            // 4. task_id 保留 + 空行已回填 + 能插新 Tag(无 group_id 不报 NOT NULL)
            using (var db = new DcDbContext(DcDbContextFactory.CreateOptions(path)))
            {
                var tags = db.Tags.OrderBy(t => t.Item).ToList();
                Assert.Equal(2, tags.Count);
                Assert.All(tags, t => Assert.Equal("task-1", t.TaskId));
                db.Tags.Add(new Tag { Id = "tag3", Item = "C", DataType = 1, TaskId = "task-1" });
                db.SaveChanges();
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
        }
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
        var tag = new Tag { Id = "tag1", Item = "ns=2;s=T", DataType = 6, TaskId = "t1", IsVirtual = false, ScaleFactor = 0.1, Offset = -5 };
        _db.Tags.Add(tag);

        var virt = new Tag { Id = "vtag1", Item = "补偿流量", DataType = 6, TaskId = "t1", IsVirtual = true };
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
