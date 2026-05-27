using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Dc.Domain.Entities;
using Dc.Infrastructure.Messaging;
using Dc.Infrastructure.Orchestration;
using Dc.Infrastructure.Persistence;
using Dc.Integration.Tests.Ua.Fixtures;
using Dc.Opc.Abstractions;
using Dc.Opc.Ua;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Dc.Integration.Tests.Ua;

[Collection("Ua")]
public class HeadlessCollectorE2ETests
{
    // 测试用最简 DbContextFactory（DcDbContext 接受 DbContextOptions）
    private sealed class TestDbContextFactory(DbContextOptions<DcDbContext> options) : IDbContextFactory<DcDbContext>
    {
        public DcDbContext CreateDbContext() => new(options);
    }

    // 无头链路端到端（纯 Linux）：SQLite 任务 → DbTaskLauncher → TaskOrchestrator
    //   → UA 订阅（内嵌 server）→ TcpPublisher → 回环 TCP sink 收到值。
    // 这正是 Dc.Cli 在 Linux/Docker 上跑的完整路径，无需 WPF、无需 Windows。
    [Fact(Timeout = 40_000)]
    public async Task HeadlessLauncher_LoadsTaskFromDb_CollectsUa_PublishesToTcp()
    {
        // 1) 内嵌 UA server
        await using var ua = new TestUaServerHost(TestUaServerHost.FindFreePort());
        await ua.StartAsync();

        // 2) 回环 TCP sink：收一帧 → 跳过 v1.1 的 magic+format-id → 解出 TagValue
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var sinkPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serializer = new MessagePackMessageSerializer();
        var sinkTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var ns = client.GetStream();
            var lenBuf = new byte[4];
            await ns.ReadExactlyAsync(lenBuf);
            var len = BinaryPrimitives.ReadInt32BigEndian(lenBuf);
            var frame = new byte[len];
            await ns.ReadExactlyAsync(frame);
            return serializer.Deserialize<TagValue>(frame[2..]);
        });

        // 3) 临时 SQLite：建库 + 插一条 UA 任务（Node=内嵌 server，TcpAddress=sink）+ 一个 Tag
        var dbPath = Path.Combine(Path.GetTempPath(), $"dc-cli-it-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<DcDbContext>()
            .UseSqlite(new SqliteConnectionStringBuilder { DataSource = dbPath, ForeignKeys = false }.ToString())
            .UseSnakeCaseNamingConvention()
            .Options;
        var dbFactory = new TestDbContextFactory(options);
        try
        {
            await using (var db = dbFactory.CreateDbContext())
            {
                await db.Database.EnsureCreatedAsync();
                db.Tasks.Add(new CollectorTask
                {
                    Id = "headless-1",
                    Type = (byte)OpcProtocol.Ua,
                    Node = ua.EndpointUrl,
                    TcpAddress = $"127.0.0.1:{sinkPort}",
                    Interval = 200,
                    Deviation = 0,
                    Tags = { new Tag { Id = "tag-1", Item = "ns=2;s=Demo.Int32", DataType = 6 } }
                });
                await db.SaveChangesAsync();
            }

            // 4) 真编排器：UA 订阅器工厂 + TCP 发布器
            await using var orch = new TaskOrchestrator(
                new IOpcSubscriberFactory[] { new OpcUaSubscriberFactory() },
                new TcpPublisherFactory(serializer));
            var launcher = new DbTaskLauncher(dbFactory, orch);

            // 5) 从 DB 拉起 UA 任务
            var (started, skipped) = await launcher.StartAllAsync(new HashSet<OpcProtocol> { OpcProtocol.Ua });
            Assert.Equal(1, started);
            Assert.Equal(0, skipped);

            // 6) sink 应收到 Demo.Int32 的值
            var received = await sinkTask.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal("ns=2;s=Demo.Int32", received.Item);
            Assert.NotNull(received.Value);
        }
        finally
        {
            try { File.Delete(dbPath); } catch { /* 忽略 */ }
        }
    }
}
