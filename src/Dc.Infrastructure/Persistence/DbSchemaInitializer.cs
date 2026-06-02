using Microsoft.EntityFrameworkCore;

namespace Dc.Infrastructure.Persistence;

// 建库 + 旧库列兼容的单一来源（WPF App 与无头 Cli 共用）。
// EnsureCreated 不跑 schema 迁移；旧库手工补字段。SQLite 无 IF NOT EXISTS 列语法，靠 try/catch 兜底。
// 注意：表名是 dc_tasks / dc_configs（DcDbContext.OnModelCreating 显式 ToTable + snake_case）。
public static class DbSchemaInitializer
{
    public static void EnsureCreated(DcDbContext db)
    {
        db.Database.EnsureCreated();
        TryAddColumn(db, "ALTER TABLE dc_tasks ADD COLUMN clsid TEXT NULL");
        TryAddColumn(db, "ALTER TABLE dc_configs ADD COLUMN dc_description TEXT NOT NULL DEFAULT ''");
    }

    private static void TryAddColumn(DcDbContext db, string sql)
    {
        // 列已存在或表不存在（新库 EnsureCreated 已建好正确 schema）时忽略。
        try { db.Database.ExecuteSqlRaw(sql); }
        catch { /* 已存在 */ }
    }
}
