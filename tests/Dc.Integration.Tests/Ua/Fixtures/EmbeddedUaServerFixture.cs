using Xunit;

namespace Dc.Integration.Tests.Ua.Fixtures;

// xunit Fixture：测试 class 用 [Collection("Ua")] 共享一个进程内 UA Server，
// 避免每个测试都从零启动（启动证书校验约 1-2s）。底层复用 TestUaServerHost。
//
// 启动后：Endpoint 暴露 opc.tcp://127.0.0.1:<random_port>，无安全 (None/None)，允许匿名。
public sealed class EmbeddedUaServerFixture : IAsyncLifetime
{
    public Uri Endpoint { get; private set; } = default!;
    public string AnonymousEndpointUrl => $"{Endpoint}";

    private TestUaServerHost? _host;

    public async Task InitializeAsync()
    {
        _host = new TestUaServerHost(TestUaServerHost.FindFreePort());
        await _host.StartAsync();
        Endpoint = _host.Endpoint;
    }

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
    }
}

[CollectionDefinition("Ua")]
public sealed class UaCollection : ICollectionFixture<EmbeddedUaServerFixture> { }
