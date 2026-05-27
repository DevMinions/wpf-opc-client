using Dc.Cli;
using Dc.Infrastructure.Messaging;
using Dc.Infrastructure.Orchestration;
using Dc.Infrastructure.Persistence;
using Dc.Opc.Abstractions;
using Dc.Opc.Ua;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

// 无头 OPC 采集器：从 sqlite.db 加载任务，跑 UA 采集 + TCP 发布，可在 Linux/Docker 部署。
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console()
    .WriteTo.File(Path.Combine(AppContext.BaseDirectory, "logs", "dc-cli-.log"), rollingInterval: RollingInterval.Day)
    .CreateLogger();

try
{
    var builder = Host.CreateApplicationBuilder(args);
    builder.Services.AddSerilog();

    // ── 持久化：复用同一套 SQLite（snake_case + dc_ 前缀），相对路径锚定 EXE 目录 ──
    var configured = builder.Configuration["Database:Path"] ?? "sqlite.db";
    var dbPath = Path.IsPathRooted(configured) ? configured : Path.Combine(AppContext.BaseDirectory, configured);
    var connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath, ForeignKeys = false }.ToString();
    builder.Services.AddDbContextFactory<DcDbContext>(o =>
        o.UseSqlite(connectionString).UseSnakeCaseNamingConvention());

    // ── 消息序列化：按 Messaging:Format 选（默认 msgpack） ──
    builder.Services.AddSingleton<MessagePackMessageSerializer>();
    builder.Services.AddSingleton<JsonMessageSerializer>();
    builder.Services.AddSingleton<IMessageSerializer>(sp =>
    {
        var formatId = builder.Configuration["Messaging:Format"]?.Trim().ToLowerInvariant() ?? "msgpack";
        return formatId switch
        {
            "json" => sp.GetRequiredService<JsonMessageSerializer>(),
            "msgpack" => sp.GetRequiredService<MessagePackMessageSerializer>(),
            _ => throw new InvalidOperationException($"未知 Messaging:Format='{formatId}'，可选: msgpack | json")
        };
    });
    builder.Services.AddSingleton(sp => new OutboundQueueOptions
    {
        Enabled = builder.Configuration.GetValue("Messaging:Queue:Enabled", false),
        Directory = builder.Configuration.GetValue<string>("Messaging:Queue:Directory") ?? "queue",
        MaxBytes = builder.Configuration.GetValue<long>("Messaging:Queue:MaxBytes", 100L * 1024 * 1024)
    });
    builder.Services.AddSingleton<IPublisherFactory, TcpPublisherFactory>();

    // ── OPC：仅注册 UA 订阅器工厂（DA/AE 走 COM，需 Windows） ──
    builder.Services.AddSingleton<IOpcSubscriberFactory, OpcUaSubscriberFactory>();

    // ── 编排器 ──
    builder.Services.AddSingleton(sp => new OrchestratorOptions
    {
        WatchdogInterval = TimeSpan.FromSeconds(builder.Configuration.GetValue("Orchestrator:WatchdogIntervalSeconds", 30)),
        HeartbeatTimeout = TimeSpan.FromSeconds(builder.Configuration.GetValue("Orchestrator:HeartbeatTimeoutSeconds", 120))
    });
    builder.Services.AddSingleton(sp => new TaskOrchestrator(
        sp.GetServices<IOpcSubscriberFactory>(),
        sp.GetRequiredService<IPublisherFactory>(),
        sp.GetRequiredService<OrchestratorOptions>(),
        sp.GetService<ILogger<TaskOrchestrator>>()));

    // ── 诊断可观测（Metrics + 结构化日志），由 Host 自动启停 ──
    builder.Services.AddSingleton(sp => new DiagnosticsReporter(
        sp.GetRequiredService<TaskOrchestrator>().GetDiagnostics,
        new DiagnosticsReporterOptions
        {
            ReportInterval = TimeSpan.FromSeconds(builder.Configuration.GetValue("Diagnostics:ReportIntervalSeconds", 30)),
            EnableLogging = builder.Configuration.GetValue("Diagnostics:EnableLogging", true),
            EnableMetrics = builder.Configuration.GetValue("Diagnostics:EnableMetrics", true)
        },
        sp.GetService<ILogger<DiagnosticsReporter>>()));
    builder.Services.AddHostedService(sp => sp.GetRequiredService<DiagnosticsReporter>());

    // ── 从 DB 拉起任务的启动器 + 主运行服务 ──
    builder.Services.AddSingleton(sp => new DbTaskLauncher(
        sp.GetRequiredService<IDbContextFactory<DcDbContext>>(),
        sp.GetRequiredService<TaskOrchestrator>(),
        sp.GetService<ILogger<DbTaskLauncher>>()));
    builder.Services.AddHostedService<CollectorRunnerService>();

    // ── OPC UA 安全基线：默认严格（AutoAccept=false / 2048-bit），与 WPF 一致，可经 appsettings 覆盖 ──
    OpcUaApplicationConfig.AutoAcceptUntrustedCertificates =
        builder.Configuration.GetValue("OpcUa:AutoAcceptUntrustedCertificates", false);
    OpcUaApplicationConfig.MinimumCertificateKeySize =
        builder.Configuration.GetValue<ushort>("OpcUa:MinimumCertificateKeySize", 2048);

    var host = builder.Build();

    // EnsureCreated + 旧库列兼容（与 WPF App.xaml.cs 一致）
    using (var scope = host.Services.CreateScope())
    {
        var dbf = scope.ServiceProvider.GetRequiredService<IDbContextFactory<DcDbContext>>();
        using var db = dbf.CreateDbContext();
        db.Database.EnsureCreated();
        try { db.Database.ExecuteSqlRaw("ALTER TABLE dc_tasks ADD COLUMN clsid TEXT NULL"); } catch { /* 已存在 */ }
        try { db.Database.ExecuteSqlRaw("ALTER TABLE dc_configs ADD COLUMN dc_description TEXT NOT NULL DEFAULT ''"); } catch { /* 已存在 */ }
    }

    Log.Information("Dc.Cli 启动，数据库 {DbPath}", dbPath);
    await host.RunAsync();
    return 0;
}
catch (Exception ex)
{
    Log.Fatal(ex, "Dc.Cli 致命错误");
    return 1;
}
finally
{
    Log.CloseAndFlush();
}
