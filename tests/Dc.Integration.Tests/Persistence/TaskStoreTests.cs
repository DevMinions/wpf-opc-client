using Dc.Domain.Entities;
using Dc.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Dc.Integration.Tests.Persistence;

public class TaskStoreTests
{
    private static DbContextOptions<DcDbContext> Options(string path) =>
        new DbContextOptionsBuilder<DcDbContext>()
            .UseSqlite(new SqliteConnectionStringBuilder { DataSource = path, ForeignKeys = false }.ToString())
            .UseSnakeCaseNamingConvention()
            .Options;

    private static string Seed(out DbContextOptions<DcDbContext> opts)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dc-store-{Guid.NewGuid():N}.db");
        opts = Options(path);
        using var db = new DcDbContext(opts);
        DbSchemaInitializer.EnsureCreated(db);
        db.Tasks.Add(new CollectorTask { Id = "task1", Server = "炉温", Node = "opc.tcp://x", Type = 2, Interval = 1000 });
        db.Groups.Add(new Group { Id = "g1", Name = "组A", TaskId = "task1" });
        db.Groups.Add(new Group { Id = "g2", Name = "组B", TaskId = "task1" });
        db.Tags.Add(new Tag { Id = "tag1", Item = "i1", TaskId = "task1", GroupId = "g1" });
        db.Tags.Add(new Tag { Id = "tag2", Item = "i2", TaskId = "task1", GroupId = "g2" });
        db.Tags.Add(new Tag { Id = "tag3", Item = "i3", TaskId = "task1", GroupId = "" });
        db.Tasks.Add(new CollectorTask { Id = "other", Server = "s", Node = "n", Type = 2 });
        db.Groups.Add(new Group { Id = "go", Name = "其它", TaskId = "other" });
        db.Tags.Add(new Tag { Id = "tago", Item = "io", TaskId = "other", GroupId = "go" });
        db.SaveChanges();
        return path;
    }

    [Fact]
    public async Task UpdateAsync_PersistsFields_PreservesCreatedAt_AdvancesUpdatedAt()
    {
        var path = Seed(out var opts);
        try
        {
            DateTime created;
            await using (var db = new DcDbContext(opts))
                created = (await db.Tasks.FindAsync("task1"))!.CreatedAt;

            var edited = new CollectorTask { Id = "task1", Server = "新名", Node = "opc.tcp://y",
                Type = 2, Interval = 2000, Deviation = 5, TcpAddress = "1.2.3.4:9", UseSecurity = false };

            await using (var db = new DcDbContext(opts))
                await TaskStore.UpdateAsync(db, edited);

            await using (var db = new DcDbContext(opts))
            {
                var t = await db.Tasks.FindAsync("task1");
                Assert.Equal("新名", t!.Server);
                Assert.Equal(2000, t.Interval);
                Assert.False(t.UseSecurity);
                Assert.Equal(created, t.CreatedAt);
                Assert.True(t.UpdatedAt >= created);
            }
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task DeleteCascadeAsync_RemovesTaskGroupsTags_NoOrphans_LeavesOtherTask()
    {
        var path = Seed(out var opts);
        try
        {
            await using (var db = new DcDbContext(opts))
                await TaskStore.DeleteCascadeAsync(db, "task1");

            await using (var db = new DcDbContext(opts))
            {
                Assert.Null(await db.Tasks.FindAsync("task1"));
                Assert.Equal(0, await db.Groups.CountAsync(g => g.TaskId == "task1"));
                Assert.Equal(0, await db.Tags.CountAsync(t => t.TaskId == "task1"));
                Assert.NotNull(await db.Tasks.FindAsync("other"));
                Assert.Equal(1, await db.Groups.CountAsync(g => g.TaskId == "other"));
                Assert.Equal(1, await db.Tags.CountAsync(t => t.TaskId == "other"));
            }
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
