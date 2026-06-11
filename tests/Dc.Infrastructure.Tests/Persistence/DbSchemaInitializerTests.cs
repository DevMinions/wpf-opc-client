using Dc.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Dc.Infrastructure.Tests.Persistence;

// 旧库列兼容：EnsureCreated 不得对已存在的列跑注定失败的 ALTER —— 那会让 EF Core
// 以 Error 级别记 "Failed executing DbCommand"，每次启动吓到用户（审查项🟡）。
public class DbSchemaInitializerTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"dc-schema-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    private DbContextOptions<DcDbContext> Options(Action<string>? errorSink = null)
    {
        var cs = new SqliteConnectionStringBuilder { DataSource = _dbPath, ForeignKeys = false }.ToString();
        var b = new DbContextOptionsBuilder<DcDbContext>().UseSqlite(cs).UseSnakeCaseNamingConvention();
        if (errorSink is not null) b.LogTo(errorSink, LogLevel.Error);
        return b.Options;
    }

    private static bool ColumnExists(DcDbContext db, string table, string column)
    {
        var conn = db.Database.GetDbConnection();
        var opened = false;
        if (conn.State != System.Data.ConnectionState.Open) { conn.Open(); opened = true; }
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = $c";
            var p = cmd.CreateParameter();
            p.ParameterName = "$c";
            p.Value = column;
            cmd.Parameters.Add(p);
            return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
        }
        finally { if (opened) conn.Close(); }
    }

    [Fact]
    public void EnsureCreated_FreshDb_LogsNoCommandError()
    {
        var errors = new List<string>();
        using var db = new DcDbContext(Options(errors.Add));

        DbSchemaInitializer.EnsureCreated(db);

        Assert.DoesNotContain(errors, l => l.Contains("Failed executing DbCommand"));
    }

    [Fact]
    public void EnsureCreated_Twice_LogsNoCommandError_And_ColumnsPresent()
    {
        using (var db = new DcDbContext(Options()))
            DbSchemaInitializer.EnsureCreated(db);

        var errors = new List<string>();
        using var db2 = new DcDbContext(Options(errors.Add));
        DbSchemaInitializer.EnsureCreated(db2);

        Assert.DoesNotContain(errors, l => l.Contains("Failed executing DbCommand"));
        Assert.True(ColumnExists(db2, "dc_tasks", "clsid"));
        Assert.True(ColumnExists(db2, "dc_configs", "dc_description"));
    }

    [Fact]
    public void EnsureCreated_OldDbMissingColumns_AddsThem()
    {
        // 模拟旧库：EnsureCreated 建全 schema 后,删掉两列,再跑一次应补回来。
        using (var seed = new DcDbContext(Options()))
        {
            seed.Database.EnsureCreated();
            seed.Database.ExecuteSqlRaw("ALTER TABLE dc_tasks DROP COLUMN clsid");
            seed.Database.ExecuteSqlRaw("ALTER TABLE dc_configs DROP COLUMN dc_description");
            Assert.False(ColumnExists(seed, "dc_tasks", "clsid"));
            Assert.False(ColumnExists(seed, "dc_configs", "dc_description"));
        }

        using var db = new DcDbContext(Options());
        DbSchemaInitializer.EnsureCreated(db);

        Assert.True(ColumnExists(db, "dc_tasks", "clsid"));
        Assert.True(ColumnExists(db, "dc_configs", "dc_description"));
    }
}
