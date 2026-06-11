using Microsoft.EntityFrameworkCore;

namespace Dc.Infrastructure.Persistence;

// 建库 + 旧库列兼容的单一来源（WPF App 与无头 Cli 共用）。
// EnsureCreated 不跑 schema 迁移；旧库手工补字段。表名是 dc_tasks / dc_configs
// （DcDbContext.OnModelCreating 显式 ToTable + snake_case）。
public static class DbSchemaInitializer
{
    public static void EnsureCreated(DcDbContext db)
    {
        db.Database.EnsureCreated();
        EnsureColumn(db, "dc_tasks", "clsid", "clsid TEXT NULL");
        EnsureColumn(db, "dc_configs", "dc_description", "dc_description TEXT NOT NULL DEFAULT ''");
    }

    // 旧库补字段：先查列是否存在，缺失才 ALTER。绝不跑注定失败的 SQL ——
    // 否则 EF Core 会以 Error 级别记 "Failed executing DbCommand"（即便上层吞了异常），
    // 每次启动在日志里冒红 ERR 吓到用户。新库 EnsureCreated 已建好正确 schema，此处即 no-op。
    private static void EnsureColumn(DcDbContext db, string table, string column, string columnDef)
    {
        if (ColumnExists(db, table, column)) return;
        db.Database.ExecuteSqlRaw($"ALTER TABLE {table} ADD COLUMN {columnDef}");
    }

    // 查列是否存在：SQLite 内置表值函数 pragma_table_info。table 是内部常量
    // （dc_tasks/dc_configs，无注入风险）内联进 pragma；column 走参数化。
    private static bool ColumnExists(DcDbContext db, string table, string column)
    {
        var conn = db.Database.GetDbConnection();
        var opened = false;
        if (conn.State != System.Data.ConnectionState.Open) { conn.Open(); opened = true; }
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = $column";
            var p = cmd.CreateParameter();
            p.ParameterName = "$column";
            p.Value = column;
            cmd.Parameters.Add(p);
            return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
        }
        finally { if (opened) conn.Close(); }
    }
}
