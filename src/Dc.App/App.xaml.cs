using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Dc.App.Composition;
using Dc.App.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Dc.Infrastructure.Persistence;
using Serilog;

namespace Dc.App;

public partial class App : Application
{
    // 用 fresh GUID，避免与 Wails 原版 SingleInstance 冲突；OSS 部署方可改自己的命名空间
    private const string SingleInstanceMutexName = "Global\\Dc.App.SingleInstance.b7c9e2a4-6f15-4d83-9e21-7c5a8b3f1d0e";
    private static Mutex? _singleInstanceMutex;
    private IHost? _host;

    public IServiceProvider Services =>
        _host?.Services ?? throw new InvalidOperationException("Host not started");

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 全局异常捕获 — 防止未处理异常静默吞掉或崩进程
        DispatcherUnhandledException += App_DispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;

        _singleInstanceMutex = new Mutex(true, SingleInstanceMutexName, out bool createdNew);
        if (!createdNew)
        {
            MessageDialog.Show("数据采集", "应用已在运行中。", MessageDialogKind.Info);
            Shutdown(0);
            return;
        }

        // 用 AppContext.BaseDirectory 而非 CWD，保证 Serilog 写入路径与 LogsView 读取路径一致
        var logDir = Path.Combine(AppContext.BaseDirectory, "logs");
        Directory.CreateDirectory(logDir);
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            // 压制框架噪声：EF Core 默认按 Information 记录每条 SQL，会淹没应用/OPC 日志。
            .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore", Serilog.Events.LogEventLevel.Warning)
            .MinimumLevel.Override("System", Serilog.Events.LogEventLevel.Warning)
            .WriteTo.Console()
            .WriteTo.File(Path.Combine(logDir, "dc-.log"), rollingInterval: RollingInterval.Day)
            .CreateLogger();

        Log.Information("Starting Dc.App");

        try
        {
            _host = Host.CreateDefaultBuilder()
                .ConfigureServices((ctx, services) =>
                {
                    // 同样用 BaseDirectory：相对路径以 EXE 所在目录为锚，与 CWD 解耦
                    var configured = ctx.Configuration["Database:Path"] ?? "sqlite.db";
                    var dbPath = Path.IsPathRooted(configured)
                        ? configured
                        : Path.Combine(AppContext.BaseDirectory, configured);
                    services.AddDcApp(dbPath);
                })
                .UseSerilog()
                .Build();

            // OPC UA 安全：默认严格（AutoAccept=false, 2048-bit）。appsettings.json 可覆盖。
            // dev 环境想跳过证书校验：OpcUa:AutoAcceptUntrustedCertificates = true。
            var uaConfig = _host.Services.GetRequiredService<IConfiguration>();
            Dc.Opc.Ua.OpcUaApplicationConfig.AutoAcceptUntrustedCertificates =
                uaConfig.GetValue("OpcUa:AutoAcceptUntrustedCertificates", defaultValue: false);
            Dc.Opc.Ua.OpcUaApplicationConfig.UseSecurity =
                uaConfig.GetValue("OpcUa:UseSecurity", defaultValue: true);
            Dc.Opc.Ua.OpcUaApplicationConfig.MinimumCertificateKeySize =
                uaConfig.GetValue<ushort>("OpcUa:MinimumCertificateKeySize", defaultValue: 2048);

            var dbFactory = _host.Services.GetRequiredService<IDbContextFactory<DcDbContext>>();
            using (var db = dbFactory.CreateDbContext())
            {
                // 建库 + 旧库列兼容，与无头 Cli 共用 DbSchemaInitializer 单一来源。
                DbSchemaInitializer.EnsureCreated(db);
            }

            await _host.StartAsync();

            // 初始化主题（读 appsettings.json:Theme，下发到 wpfui ApplicationThemeManager）
            var themeSvc = _host.Services.GetRequiredService<Dc.App.Services.Theme.IThemeService>();
            themeSvc.Initialize();

            var window = Services.GetRequiredService<Dc.App.Views.Shell.ShellWindow>();
            // 非模态 toast 浮层接线：必须在第一次可能弹 toast 前（Show 前最稳）把 ShellWindow 的
            // SnackbarPresenter 注入 ISnackbarService，否则后续 ShowError 无承载控件。
            Services.GetRequiredService<Wpf.Ui.ISnackbarService>()
                .SetSnackbarPresenter(window.RootSnackbarPresenter);
            // Strategy B (shutting-down-wpf-gracefully): OnExplicitShutdown + MainWindow.Closed
            // 窗口关闭后在此做异步清理，最后调 Shutdown() 结束进程
            window.Closed += OnMainWindowClosed;
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Application failed to start");
            // 启动失败的兜底路径:此时 MainWindow/host 可能未就绪,保留原生 MessageBox 以保鲁棒性
            // (自定义圆角窗依赖资源加载,异常态下可能二次失败)。
            MessageBox.Show($"启动失败：{ex.Message}", "Dc.App", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    /// <summary>
    /// Strategy B: 窗口关闭后做异步清理（host.StopAsync + Dispose），最后 Shutdown。
    /// ShutdownMode=OnExplicitShutdown 保证了进程不会在窗口关闭时提前退出。
    /// </summary>
    private async void OnMainWindowClosed(object? sender, EventArgs e)
    {
        try
        {
            if (_host != null)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await _host.StopAsync(cts.Token);
                _host.Dispose();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Cleanup failed during shutdown");
        }
        finally
        {
            Shutdown();
        }
    }

    /// <summary>
    /// OnExit 只做同步收尾（日志 flush + mutex 释放）。异步清理已在 OnMainWindowClosed 中完成。
    /// </summary>
    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        Log.CloseAndFlush();
        base.OnExit(e);
    }

    // ---- 全局异常处理器 ----

    private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Unhandled UI thread exception");
        e.Handled = true; // 阻止默认崩溃行为
    }

    private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
            Log.Fatal(ex, "Unhandled AppDomain exception (isTerminating={IsTerminating})", e.IsTerminating);
        else
            Log.Fatal("Unhandled AppDomain exception (non-Exception object: {Obj})", e.ExceptionObject);
    }

    private void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Unobserved task exception");
        e.SetObserved(); // 标记已观察，阻止默认进程终止
    }
}
