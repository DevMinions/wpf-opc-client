using Dc.App.Services;
using Dc.App.ViewModels;
using Dc.Infrastructure.Backup;
using Dc.Infrastructure.Excel;
using Dc.Infrastructure.Messaging;
using Dc.Infrastructure.Orchestration;
using Dc.Infrastructure.Persistence;
using Dc.Opc.Abstractions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dc.App.Composition;

public static class ServiceRegistration
{
    public static IServiceCollection AddDcApp(this IServiceCollection services, string sqliteFilePath)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = sqliteFilePath,
            ForeignKeys = false
        }.ToString();

        services.AddDbContextFactory<DcDbContext>(opts =>
            opts.UseSqlite(connectionString).UseSnakeCaseNamingConvention());

        // 注册所有序列化器；按 Messaging:Format 配置挑选具体实现（默认 msgpack）
        services.AddSingleton<MessagePackMessageSerializer>();
        services.AddSingleton<JsonMessageSerializer>();
        services.AddSingleton<IMessageSerializer>(sp =>
        {
            var cfg = sp.GetService<IConfiguration>();
            var formatId = cfg?["Messaging:Format"]?.Trim().ToLowerInvariant() ?? "msgpack";
            return formatId switch
            {
                "json"    => sp.GetRequiredService<JsonMessageSerializer>(),
                "msgpack" => sp.GetRequiredService<MessagePackMessageSerializer>(),
                _ => throw new InvalidOperationException(
                    $"未知 Messaging:Format='{formatId}'，可选: msgpack | json")
            };
        });
        // 断网缓存重发：默认 disabled，appsettings.json "Messaging:Queue" 可开
        services.AddSingleton<OutboundQueueOptions>(sp =>
        {
            var cfg = sp.GetService<IConfiguration>();
            if (cfg is null) return new OutboundQueueOptions();
            return new OutboundQueueOptions
            {
                Enabled = cfg.GetValue("Messaging:Queue:Enabled", false),
                Directory = cfg.GetValue<string>("Messaging:Queue:Directory") ?? "queue",
                MaxBytes = cfg.GetValue<long>("Messaging:Queue:MaxBytes", 100L * 1024 * 1024)
            };
        });
        services.AddSingleton<IPublisherFactory, TcpPublisherFactory>();
        services.AddSingleton<OrchestratorOptions>(sp =>
        {
            var config = sp.GetService<IConfiguration>();
            if (config is null) return new OrchestratorOptions();
            return new OrchestratorOptions
            {
                WatchdogInterval = TimeSpan.FromSeconds(config.GetValue("Orchestrator:WatchdogIntervalSeconds", 30)),
                HeartbeatTimeout = TimeSpan.FromSeconds(config.GetValue("Orchestrator:HeartbeatTimeoutSeconds", 120))
            };
        });

        services.AddSingleton<TaskOrchestrator>(sp => new TaskOrchestrator(
            sp.GetServices<IOpcSubscriberFactory>(),
            sp.GetRequiredService<IPublisherFactory>(),
            sp.GetRequiredService<OrchestratorOptions>(),
            sp.GetService<Microsoft.Extensions.Logging.ILogger<TaskOrchestrator>>()));

        services.AddSingleton<IOpcSubscriberFactory, Dc.Opc.Ua.OpcUaSubscriberFactory>();
        services.AddSingleton<IOpcSubscriberFactory, Dc.Opc.Da.OpcDaSubscriberFactory>();
        services.AddSingleton<IOpcSubscriberFactory, Dc.Opc.Ae.OpcAeSubscriberFactory>();
        services.AddSingleton<IOpcBrowserFactory, Dc.Opc.Ua.OpcUaBrowserFactory>();
        services.AddSingleton<IOpcBrowserFactory, Dc.Opc.Da.OpcDaBrowserFactory>();
        services.AddSingleton<IOpcBrowserFactory, Dc.Opc.Ae.OpcAeBrowserFactory>();

        services.AddSingleton<ITaskEditorDialog, TaskEditorDialog>();
        services.AddSingleton<IGroupEditorDialog, GroupEditorDialog>();
        services.AddSingleton<ITagEditorDialog, TagEditorDialog>();
        services.AddSingleton<IConfigEditorDialog, ConfigEditorDialog>();
        services.AddSingleton<ITagExcelService, ClosedXmlTagExcelService>();
        services.AddSingleton<IFilePicker, WpfFilePicker>();
        services.AddSingleton<IBrowseDialog, WpfBrowseDialog>();
        services.AddSingleton<IConfigBackupService, JsonConfigBackupService>();

        // === Shell + Theme + Navigation（S1 新增） ===
        services.AddSingleton<Dc.App.Services.Theme.IThemeApplier, Dc.App.Services.Theme.WpfUiThemeApplier>();
        services.AddSingleton<Dc.App.Services.Theme.IThemePreferenceWriter>(_ =>
            new Dc.App.Services.Theme.JsonThemePreferenceWriter(
                System.IO.Path.Combine(AppContext.BaseDirectory, "appsettings.json")));
        services.AddSingleton<Dc.App.Services.Theme.ISystemThemeWatcher,
                              Dc.App.Services.Theme.SystemEventsThemeWatcher>();
        services.AddSingleton<Dc.App.Services.Theme.IThemeService, Dc.App.Services.Theme.ThemeService>();

        services.AddSingleton<Dc.App.Navigation.INavigationService>(sp =>
            new Dc.App.Navigation.NavigationService(
                sp,
                new[]
                {
                    new Dc.App.Navigation.NavigationRoute("dashboard",   "仪表盘",   "Home24",                typeof(Dc.App.ViewModels.Dashboard.DashboardViewModel)),
                    new Dc.App.Navigation.NavigationRoute("workspace",   "采集任务", "TaskListSquareLtr24",   typeof(Dc.App.ViewModels.Workspace.TaskWorkspaceViewModel), GroupHeader: "采集"),
                    new Dc.App.Navigation.NavigationRoute("browse",      "浏览节点", "Search24",              typeof(BrowseViewModel)),
                    new Dc.App.Navigation.NavigationRoute("livedata",    "实时数据", "DataHistogram24",       typeof(LiveDataViewModel),       GroupHeader: "全局监控"),
                    new Dc.App.Navigation.NavigationRoute("diagnostics", "诊断",     "Pulse24",               typeof(DiagnosticsViewModel)),
                    new Dc.App.Navigation.NavigationRoute("settings",    "设置",     "Settings24",            typeof(SettingsViewModel),       GroupHeader: "系统"),
                    new Dc.App.Navigation.NavigationRoute("logs",        "日志",     "DocumentText24",        typeof(LogsViewModel))
                },
                footerAbout: null));

        services.AddSingleton<Dc.App.Views.Shell.ShellWindow>();
        services.AddSingleton<Dc.App.ViewModels.Shell.ShellViewModel>();
        // DashboardOrchestratorView 适配 TaskOrchestrator 到 IDashboardOrchestratorView
        services.AddSingleton<Dc.App.ViewModels.Dashboard.IDashboardOrchestratorView>(sp =>
            new Dc.App.ViewModels.Dashboard.TaskOrchestratorView(
                sp.GetRequiredService<TaskOrchestrator>()));

        services.AddSingleton<Dc.App.ViewModels.Dashboard.DashboardViewModel>(sp =>
        {
            var orchView = sp.GetRequiredService<Dc.App.ViewModels.Dashboard.IDashboardOrchestratorView>();
            var opts = sp.GetRequiredService<OrchestratorOptions>();
            return new Dc.App.ViewModels.Dashboard.DashboardViewModel(
                orchView,
                () => DateTimeOffset.UtcNow,
                opts.HeartbeatTimeout);
        });

        // 采集任务工作台（S3a）
        services.AddSingleton<Dc.App.ViewModels.Workspace.IWorkspaceTaskSource,
                              Dc.App.ViewModels.Workspace.DbWorkspaceTaskSource>();
        services.AddSingleton<Dc.App.ViewModels.Workspace.WorkspaceOverviewViewModel>(sp =>
            new Dc.App.ViewModels.Workspace.WorkspaceOverviewViewModel(
                sp.GetRequiredService<Dc.App.ViewModels.Dashboard.IDashboardOrchestratorView>(),
                () => DateTimeOffset.UtcNow));
        // GroupsViewModel 仅工作台用（无全局导航路由），复用单例即可
        services.AddSingleton<Dc.App.ViewModels.Workspace.IEmbeddableGroupPanel>(
            sp => sp.GetRequiredService<GroupsViewModel>());
        // 全局监控分离（S4）：LiveData/Diagnostics 同时挂「全局监控」导航与工作台 tab，
        // 工作台用独立实例，避免工作台设的 TaskFilter/TaskScope 污染全局视图。
        services.AddSingleton<Dc.App.ViewModels.Workspace.IEmbeddableLivePanel>(
            sp => new LiveDataViewModel(sp.GetRequiredService<TaskOrchestrator>(),
                System.Windows.Application.Current?.Dispatcher ?? System.Windows.Threading.Dispatcher.CurrentDispatcher));
        services.AddSingleton<Dc.App.ViewModels.Workspace.IEmbeddableDiagPanel>(
            sp => new DiagnosticsViewModel(sp.GetRequiredService<TaskOrchestrator>()));
        services.AddSingleton<Dc.App.ViewModels.Workspace.WorkspaceConfigViewModel>(
            sp => new Dc.App.ViewModels.Workspace.WorkspaceConfigViewModel(
                sp.GetRequiredService<Dc.App.Services.ITaskEditorDialog>()));
        services.AddSingleton<Dc.App.ViewModels.Workspace.TaskWorkspaceViewModel>(sp =>
            new Dc.App.ViewModels.Workspace.TaskWorkspaceViewModel(
                sp.GetRequiredService<Dc.App.ViewModels.Workspace.IWorkspaceTaskSource>(),
                sp.GetRequiredService<Dc.App.ViewModels.Dashboard.IDashboardOrchestratorView>(),
                () => DateTimeOffset.UtcNow,
                sp.GetRequiredService<OrchestratorOptions>().HeartbeatTimeout,
                sp.GetRequiredService<Dc.App.ViewModels.Workspace.WorkspaceOverviewViewModel>(),
                sp.GetRequiredService<TagsViewModel>(),
                sp.GetRequiredService<TaskOrchestrator>(),
                sp.GetRequiredService<Dc.App.Services.ITaskEditorDialog>(),
                sp.GetRequiredService<Dc.App.ViewModels.Workspace.IEmbeddableGroupPanel>(),
                sp.GetRequiredService<Dc.App.ViewModels.Workspace.IEmbeddableLivePanel>(),
                sp.GetRequiredService<Dc.App.ViewModels.Workspace.IEmbeddableDiagPanel>(),
                sp.GetRequiredService<Dc.App.ViewModels.Workspace.WorkspaceConfigViewModel>()));

        // === 旧 VM 保留（其他 View 由 Shell 路由继续承载） ===
        services.AddSingleton<System.Windows.Threading.Dispatcher>(
            _ => System.Windows.Application.Current?.Dispatcher
                 ?? System.Windows.Threading.Dispatcher.CurrentDispatcher);
        services.AddSingleton<GroupsViewModel>();
        services.AddSingleton<TagsViewModel>();
        services.AddSingleton<LiveDataViewModel>();
        services.AddSingleton<BrowseViewModel>();
        services.AddSingleton<DiagnosticsViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<LogsViewModel>();

        return services;
    }
}
