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
        EnsureColumn(db, "dc_tasks", "name", "name TEXT NULL");
        EnsureColumn(db, "dc_configs", "dc_description", "dc_description TEXT NOT NULL DEFAULT ''");
        EnsureColumn(db, "dc_tasks", "use_security", "use_security INTEGER NOT NULL DEFAULT 1");
        EnsureColumn(db, "dc_tags", "scale_factor", "scale_factor REAL NULL");
        EnsureColumn(db, "dc_tags", "offset", "offset REAL NULL");
        EnsureColumn(db, "dc_tags", "is_virtual", "is_virtual INTEGER NOT NULL DEFAULT 0");
        EnsureTable(db, "dc_formulas", """
            CREATE TABLE IF NOT EXISTS dc_formulas (
                id TEXT NOT NULL PRIMARY KEY,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                name TEXT NOT NULL,
                expression TEXT NOT NULL,
                output_tag_id TEXT NOT NULL,
                output_unit TEXT NULL,
                task_id TEXT NOT NULL
            )
            """);
        EnsureTable(db, "dc_formula_inputs", """
            CREATE TABLE IF NOT EXISTS dc_formula_inputs (
                id TEXT NOT NULL PRIMARY KEY,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                formula_id TEXT NOT NULL,
                alias TEXT NOT NULL,
                source_tag_id TEXT NOT NULL
            )
            """);
        EnsureIndex(db, "udx_formula_name", "CREATE UNIQUE INDEX IF NOT EXISTS udx_formula_name ON dc_formulas (task_id, name)");
        EnsureIndex(db, "udx_formula_input_alias", "CREATE UNIQUE INDEX IF NOT EXISTS udx_formula_input_alias ON dc_formula_inputs (formula_id, alias)");
        MigrateDropGroupLayer(db);
    }

    // 分组层移除迁移(旧库一次性):dc_tags.group_id 列 + dc_groups 表去除,Tag 直接挂任务。
    // 仅当旧 schema(dc_tags 仍有 group_id 列)时执行;新库 EnsureCreated 已建对 → no-op。幂等。
    // 旧 dc_tags 上 group_id 带外键约束,SQLite 不能 DROP 被外键引用的列(会报
    // "unknown column in foreign key definition"),只能整表重建(EnsureColumn 已先补齐 scale_factor 等列)。
    private static void MigrateDropGroupLayer(DcDbContext db)
    {
        if (!TableExists(db, "dc_tags")) return;
        if (!ColumnExists(db, "dc_tags", "group_id")) return; // 已是新 schema

        // 显式保持连接打开:PRAGMA foreign_keys 只在「连接打开期间、事务外」逐条生效;若让每条
        // ExecuteSqlRaw 各自开关连接,PRAGMA 会丢。旧库可能 FK=ON 且有孤儿行(task_id 无对应任务),
        // 整表重建期间必须关 FK,否则 DROP/RENAME 触发 FK 检查中断迁移。
        db.Database.OpenConnection();
        try
        {
            db.Database.ExecuteSqlRaw("PRAGMA foreign_keys=OFF");

            // 1. 防御性回填:个别旧行 task_id 可能为空,从其分组补回。(正常每 Tag 建时即带 task_id)
            if (TableExists(db, "dc_groups"))
            {
                db.Database.ExecuteSqlRaw(
                    "UPDATE dc_tags SET task_id = (SELECT task_id FROM dc_groups WHERE dc_groups.id = dc_tags.group_id) " +
                    "WHERE (task_id IS NULL OR task_id = '') AND group_id IN (SELECT id FROM dc_groups)");
            }
            // 2. 整表重建去掉 group_id 及其外键(SQLite 不能 DROP 被外键引用的列)。
            db.Database.ExecuteSqlRaw("DROP INDEX IF EXISTS udx_name");
            db.Database.ExecuteSqlRaw(
                """
                CREATE TABLE dc_tags_new (
                    id TEXT NOT NULL PRIMARY KEY,
                    item TEXT NOT NULL,
                    data_type INTEGER NOT NULL,
                    task_id TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    scale_factor REAL NULL,
                    "offset" REAL NULL,
                    is_virtual INTEGER NOT NULL DEFAULT 0
                )
                """);
            db.Database.ExecuteSqlRaw(
                "INSERT INTO dc_tags_new (id, item, data_type, task_id, created_at, updated_at, scale_factor, \"offset\", is_virtual) " +
                "SELECT id, item, data_type, task_id, created_at, updated_at, scale_factor, \"offset\", is_virtual FROM dc_tags");
            db.Database.ExecuteSqlRaw("DROP TABLE dc_tags");
            db.Database.ExecuteSqlRaw("ALTER TABLE dc_tags_new RENAME TO dc_tags");
            db.Database.ExecuteSqlRaw("CREATE UNIQUE INDEX IF NOT EXISTS udx_name ON dc_tags (item, task_id)");
            // 3. 删分组表。
            db.Database.ExecuteSqlRaw("DROP TABLE IF EXISTS dc_groups");
        }
        finally { db.Database.CloseConnection(); }
    }

    // 旧库补字段：先查列是否存在，缺失才 ALTER。绝不跑注定失败的 SQL ——
    // 否则 EF Core 会以 Error 级别记 "Failed executing DbCommand"（即便上层吞了异常），
    // 每次启动在日志里冒红 ERR 吓到用户。新库 EnsureCreated 已建好正确 schema，此处即 no-op。
    private static void EnsureColumn(DcDbContext db, string table, string column, string columnDef)
    {
        // 表不存在时 ALTER 必然失败（同样冒红 ERR）。旧库可能只含部分表，先确认表在再补列。
        if (!TableExists(db, table)) return;
        if (ColumnExists(db, table, column)) return;
        db.Database.ExecuteSqlRaw($"ALTER TABLE {table} ADD COLUMN {columnDef}");
    }

    // 查表是否存在：sqlite_master 单一来源，table 是内部常量（无注入风险）走参数化。
    private static bool TableExists(DcDbContext db, string table)
    {
        var conn = db.Database.GetDbConnection();
        var opened = false;
        if (conn.State != System.Data.ConnectionState.Open) { conn.Open(); opened = true; }
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $table";
            var p = cmd.CreateParameter();
            p.ParameterName = "$table";
            p.Value = table;
            cmd.Parameters.Add(p);
            return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
        }
        finally { if (opened) conn.Close(); }
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

    private static void EnsureTable(DcDbContext db, string table, string createSql)
    {
        if (TableExists(db, table)) return;
        db.Database.ExecuteSqlRaw(createSql);
    }

    private static void EnsureIndex(DcDbContext db, string indexName, string createSql)
    {
        var conn = db.Database.GetDbConnection();
        var opened = false;
        if (conn.State != System.Data.ConnectionState.Open) { conn.Open(); opened = true; }
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name=$n";
            var p = cmd.CreateParameter();
            p.ParameterName = "$n";
            p.Value = indexName;
            cmd.Parameters.Add(p);
            if (Convert.ToInt64(cmd.ExecuteScalar()) > 0) return;
            db.Database.ExecuteSqlRaw(createSql);
        }
        finally { if (opened) conn.Close(); }
    }
}
