using Dc.Infrastructure.Messaging;
using Xunit;

namespace Dc.Infrastructure.Tests.Messaging;

public class BatchingTcpPublisherHealthTests
{
    [Fact]
    public async Task Health_DelegatesQueuePendingAndDropped()
    {
        var path = Path.Combine(Path.GetTempPath(), $"btp-test-{Guid.NewGuid():N}.bin");
        try
        {
            using var queue = new OutboundQueue(path, maxBytes: 1024 * 1024);
            queue.Enqueue(new byte[50]);

            // 指向一个不会有人听的端口；只验证 health 委托读队列，不真发送。
            await using var pub = new BatchingTcpPublisher("127.0.0.1", 1, new JsonMessageSerializer(), queue);
            var health = (IPublisherHealth)pub;

            Assert.Equal(queue.PendingBytes, health.PendingBytes);
            Assert.True(health.PendingBytes > 0);
            Assert.Equal(queue.DroppedFrameCount, health.DroppedFrameCount);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            var cursor = path + ".cursor";
            if (File.Exists(cursor)) File.Delete(cursor);
        }
    }

    [Fact]
    public async Task Health_NoQueue_ReturnsZero()
    {
        await using var pub = new BatchingTcpPublisher("127.0.0.1", 1, new JsonMessageSerializer(), queue: null);
        var health = (IPublisherHealth)pub;
        Assert.Equal(0, health.PendingBytes);
        Assert.Equal(0, health.DroppedFrameCount);
    }
}
