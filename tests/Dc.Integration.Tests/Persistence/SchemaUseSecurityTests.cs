using Dc.Domain.Entities;
using Dc.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Dc.Integration.Tests.Persistence;

public class SchemaUseSecurityTests
{
    private static DbContextOptions<DcDbContext> Options(string path) =>
        new DbContextOptionsBuilder<DcDbContext>()
            .UseSqlite(new SqliteConnectionStringBuilder { DataSource = path, ForeignKeys = false }.ToString())
            .UseSnakeCaseNamingConvention()
            .Options;

    [Fact]
    public async Task NewDb_UseSecurity_RoundTrips_DefaultTrue()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dc-sec-{Guid.NewGuid():N}.db");
        try
        {
            await using (var db = new DcDbContext(Options(path)))
            {
                DbSchemaInitializer.EnsureCreated(db);
                db.Tasks.Add(new CollectorTask { Id = "t1", Server = "s", Node = "n", Type = 2 });
                db.Tasks.Add(new CollectorTask { Id = "t2", Server = "s", Node = "n", Type = 2, UseSecurity = false });
                await db.SaveChangesAsync();
            }
            await using (var db = new DcDbContext(Options(path)))
            {
                Assert.True((await db.Tasks.FindAsync("t1"))!.UseSecurity);
                Assert.False((await db.Tasks.FindAsync("t2"))!.UseSecurity);
            }
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task OldDb_WithoutColumn_GetsColumnDefaultTrue()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dc-sec-old-{Guid.NewGuid():N}.db");
        try
        {
            await using (var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString()))
            {
                await conn.OpenAsync();
                var cmd = conn.CreateCommand();
                cmd.CommandText = @"CREATE TABLE dc_tasks (id TEXT PRIMARY KEY, server TEXT, node TEXT, clsid TEXT,
                    type INTEGER, interval INTEGER, deviation INTEGER, tcp_address TEXT, created_at TEXT, updated_at TEXT);
                    INSERT INTO dc_tasks (id, server, node, type, interval, deviation, tcp_address, created_at, updated_at)
                    VALUES ('old1', 's', 'n', 2, 0, 0, '', '2024-01-01 00:00:00', '2024-01-01 00:00:00');";
                await cmd.ExecuteNonQueryAsync();
            }
            await using (var db = new DcDbContext(Options(path)))
                DbSchemaInitializer.EnsureCreated(db);
            await using (var db = new DcDbContext(Options(path)))
                Assert.True((await db.Tasks.FindAsync("old1"))!.UseSecurity);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
