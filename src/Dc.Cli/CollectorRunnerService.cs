using Dc.Infrastructure.Orchestration;
using Dc.Opc.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dc.Cli;

// 无头采集主服务：进程启动时从 DB 拉起所有受支持任务，之后由 TaskOrchestrator 在后台持续采集/发布
// （含心跳看门狗与 UA 断线自动重连），直到收到 Ctrl+C / SIGTERM 关停。
internal sealed class CollectorRunnerService : BackgroundService
{
    // 无头 Linux 构建仅支持 UA（DA/AE 走 COM，需 Windows）。
    private static readonly IReadOnlySet<OpcProtocol> Supported =
        new HashSet<OpcProtocol> { OpcProtocol.Ua };

    private readonly DbTaskLauncher _launcher;
    private readonly ILogger<CollectorRunnerService> _logger;

    public CollectorRunnerService(DbTaskLauncher launcher, ILogger<CollectorRunnerService> logger)
    {
        _launcher = launcher;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("无头采集启动（仅 UA；DA/AE 需 Windows COM 构建）…");
        try
        {
            var (started, skipped) = await _launcher.StartAllAsync(Supported, stoppingToken).ConfigureAwait(false);
            _logger.LogInformation("启动完成：已启动 {Started}，跳过 {Skipped}。按 Ctrl+C 退出。", started, skipped);
        }
        catch (OperationCanceledException) { /* 启动过程中被关停 */ }
        catch (Exception ex)
        {
            _logger.LogError(ex, "加载/启动任务失败");
        }
        // ExecuteAsync 返回后宿主继续运行：orchestrator 的采集管线/看门狗在后台跑，直到关停信号。
    }
}
