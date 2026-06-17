using Dc.Domain.Entities;
using Dc.Infrastructure.Persistence;
using Dc.Opc.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Dc.Infrastructure.Orchestration;

// 从数据库加载采集任务并经 TaskOrchestrator 启动。
// 无头 Cli 用它一次性拉起所有受支持任务；CollectorTask→TaskStartRequest 的映射与 WPF 一致，可共用。
public sealed class DbTaskLauncher
{
    private readonly IDbContextFactory<DcDbContext> _dbFactory;
    private readonly TaskOrchestrator _orchestrator;
    private readonly ILogger<DbTaskLauncher>? _logger;

    public DbTaskLauncher(
        IDbContextFactory<DcDbContext> dbFactory,
        TaskOrchestrator orchestrator,
        ILogger<DbTaskLauncher>? logger = null)
    {
        _dbFactory = dbFactory;
        _orchestrator = orchestrator;
        _logger = logger;
    }

    // CollectorTask → TaskStartRequest 的纯映射，连接选项口径单一来源（WPF 与无头 Cli 共用）。
    // 无头 Cli 用 task.Tags（已 Include）；WPF 侧 Tag 单独加载，走下面的显式 tags 重载。
    public static TaskStartRequest ToStartRequest(CollectorTask task) =>
        ToStartRequest(task, task.Tags.Select(t => new TagDescriptor(t.Id, t.Item, t.DataType)).ToList());

    public static TaskStartRequest ToStartRequest(CollectorTask task, IReadOnlyCollection<TagDescriptor> tags)
    {
        var protocol = (OpcProtocol)task.Type;

        // UA：opc.tcp URL 可能落在 Server（编辑器「服务器」字段——用户直觉输入处）
        // 或 Node（历史/测试约定）。Server 是 UA URL 时取 Server，避免「localhost」被当 discoveryUrl 致 UriFormatException。
        // DA/AE：维持原口径——Node 当 host（ServerUri），Server 当 ProgID（ServerProgId）。
        var serverUri = task.Node;
        var serverProgId = task.Server;
        if (protocol == OpcProtocol.Ua && IsUaUrl(task.Server))
        {
            serverUri = task.Server;
            serverProgId = null;
        }

        return new TaskStartRequest(
            task.Id,
            protocol,
            new OpcConnectionOptions
            {
                ServerUri = serverUri,
                ServerProgId = serverProgId,
                ServerClsid = task.Clsid,
                SamplingInterval = TimeSpan.FromMilliseconds(Math.Max(task.Interval, 1)),
                DeadbandPercent = task.Deviation
            },
            task.TcpAddress,
            tags);
    }

    private static bool IsUaUrl(string? s) =>
        !string.IsNullOrWhiteSpace(s) && s!.TrimStart().StartsWith("opc.tcp", StringComparison.OrdinalIgnoreCase);

    // 加载所有任务，启动 supportedProtocols 内、且至少有一个 Tag 的任务。返回 (已启动, 已跳过)。
    public async Task<(int Started, int Skipped)> StartAllAsync(
        IReadOnlySet<OpcProtocol> supportedProtocols, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var tasks = await db.Tasks.AsNoTracking()
            .Include(t => t.Tags)
            .ToListAsync(ct).ConfigureAwait(false);

        int started = 0, skipped = 0;
        foreach (var task in tasks)
        {
            var protocol = (OpcProtocol)task.Type;
            if (!supportedProtocols.Contains(protocol))
            {
                _logger?.LogWarning("跳过任务 {Id}（{Node}）：协议 {Protocol} 当前构建不支持", task.Id, task.Node, protocol);
                skipped++;
                continue;
            }
            if (task.Tags.Count == 0)
            {
                _logger?.LogWarning("跳过任务 {Id}（{Node}）：无 Tag", task.Id, task.Node);
                skipped++;
                continue;
            }
            try
            {
                await _orchestrator.StartAsync(ToStartRequest(task), ct).ConfigureAwait(false);
                started++;
                _logger?.LogInformation("已启动任务 {Id}：{Node} → {Tcp}（{Tags} tags）",
                    task.Id, task.Node, task.TcpAddress, task.Tags.Count);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "启动任务 {Id} 失败", task.Id);
                skipped++;
            }
        }
        return (started, skipped);
    }
}
