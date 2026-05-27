using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Dc.Infrastructure.Persistence;

public static class DcDbContextFactory
{
    public static DbContextOptions<DcDbContext> CreateOptions(string sqliteFilePath)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = sqliteFilePath,
            ForeignKeys = false
        }.ToString();

        return new DbContextOptionsBuilder<DcDbContext>()
            .UseSqlite(connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
    }
}
