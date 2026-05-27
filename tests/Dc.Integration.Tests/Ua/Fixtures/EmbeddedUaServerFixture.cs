using System.Net;
using System.Net.Sockets;
using Opc.Ua;
using Opc.Ua.Configuration;
using Xunit;

namespace Dc.Integration.Tests.Ua.Fixtures;

// xunit Fixture：测试 class 用 [Collection("Ua")] 共享一个进程内 UA Server，
// 避免每个测试都从零启动（启动证书校验约 1-2s）。
//
// 启动后：Endpoint 暴露 opc.tcp://127.0.0.1:<random_port>，无安全 (None/None)，允许匿名。
public sealed class EmbeddedUaServerFixture : IAsyncLifetime
{
    public Uri Endpoint { get; private set; } = default!;
    public string AnonymousEndpointUrl => $"{Endpoint}";

    private ApplicationInstance? _app;
    private MinimalUaServer? _server;
    private string? _pkiRoot;

    public async Task InitializeAsync()
    {
        var port = FindFreePort();
        Endpoint = new Uri($"opc.tcp://127.0.0.1:{port}");

        // 隔离 PKI 到临时目录，避免污染开发者机器的 ApplicationData
        _pkiRoot = Path.Combine(Path.GetTempPath(), "dc-it-ua-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(_pkiRoot);

        var config = BuildConfig(port, _pkiRoot);
        config.Validate(ApplicationType.Server).GetAwaiter().GetResult();

        _app = new ApplicationInstance
        {
            ApplicationName = "Dc.IntegrationTest.UaServer",
            ApplicationType = ApplicationType.Server,
            ApplicationConfiguration = config
        };

        // 首次会生成 self-signed 证书；隔离到 _pkiRoot 不污染外面
        await _app.CheckApplicationInstanceCertificate(silent: true, minimumKeySize: 2048);

        _server = new MinimalUaServer();
        await _app.Start(_server);
    }

    public Task DisposeAsync()
    {
        try { _server?.Stop(); } catch { }
        try
        {
            if (_pkiRoot is not null && Directory.Exists(_pkiRoot))
                Directory.Delete(_pkiRoot, recursive: true);
        }
        catch { }
        return Task.CompletedTask;
    }

    private static int FindFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static ApplicationConfiguration BuildConfig(int port, string pkiRoot)
    {
        return new ApplicationConfiguration
        {
            ApplicationName = "Dc.IntegrationTest.UaServer",
            ApplicationUri = "urn:localhost:dc:integrationtest:uaserver",
            ApplicationType = ApplicationType.Server,
            ProductUri = "https://git.adamyu.top/dc",
            SecurityConfiguration = new SecurityConfiguration
            {
                ApplicationCertificate = new CertificateIdentifier
                {
                    StoreType = "Directory",
                    StorePath = Path.Combine(pkiRoot, "own"),
                    SubjectName = "CN=Dc.IntegrationTest.UaServer, C=US, S=Test, O=Dc, DC=localhost"
                },
                TrustedIssuerCertificates = new CertificateTrustList
                {
                    StoreType = "Directory", StorePath = Path.Combine(pkiRoot, "issuers")
                },
                TrustedPeerCertificates = new CertificateTrustList
                {
                    StoreType = "Directory", StorePath = Path.Combine(pkiRoot, "trusted")
                },
                RejectedCertificateStore = new CertificateTrustList
                {
                    StoreType = "Directory", StorePath = Path.Combine(pkiRoot, "rejected")
                },
                AutoAcceptUntrustedCertificates = true,
                AddAppCertToTrustedStore = true,
                MinimumCertificateKeySize = 2048
            },
            TransportConfigurations = new TransportConfigurationCollection(),
            TransportQuotas = new TransportQuotas { OperationTimeout = 15_000 },
            ServerConfiguration = new ServerConfiguration
            {
                BaseAddresses = { $"opc.tcp://127.0.0.1:{port}" },
                SecurityPolicies =
                {
                    new ServerSecurityPolicy
                    {
                        SecurityMode = MessageSecurityMode.None,
                        SecurityPolicyUri = SecurityPolicies.None
                    }
                },
                UserTokenPolicies = { new UserTokenPolicy(UserTokenType.Anonymous) },
                DiagnosticsEnabled = false,
                MinRequestThreadCount = 5,
                MaxRequestThreadCount = 100,
                MaxQueuedRequestCount = 200
            },
            TraceConfiguration = new TraceConfiguration { TraceMasks = 0 }
        };
    }
}

[CollectionDefinition("Ua")]
public sealed class UaCollection : ICollectionFixture<EmbeddedUaServerFixture> { }
