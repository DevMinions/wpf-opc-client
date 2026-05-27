using System.Net;
using System.Net.Sockets;
using Opc.Ua;
using Opc.Ua.Configuration;

namespace Dc.Integration.Tests.Ua.Fixtures;

// 可停可重启的进程内 UA Server 宿主。
// - 断线重连测试用它在「同端口」Stop→Start 模拟服务器崩溃后恢复。
// - EmbeddedUaServerFixture 也复用它做共享 server（单一 config 来源，避免重复）。
// 端点：opc.tcp://127.0.0.1:<port>，无安全 (None/None)，允许匿名。
internal sealed class TestUaServerHost : IAsyncDisposable
{
    public int Port { get; }
    public Uri Endpoint => new($"opc.tcp://127.0.0.1:{Port}");
    public string EndpointUrl => Endpoint.ToString();

    private readonly string _pkiRoot;
    private readonly bool _ownsPki;
    private ApplicationInstance? _app;
    private MinimalUaServer? _server;

    // pkiRoot 为空则自建临时目录并负责清理；传入则复用（重启时保留同一套 server 证书）。
    public TestUaServerHost(int port, string? pkiRoot = null)
    {
        Port = port;
        if (pkiRoot is null)
        {
            _pkiRoot = Path.Combine(Path.GetTempPath(), "dc-it-ua-" + Guid.NewGuid().ToString("N")[..8]);
            _ownsPki = true;
        }
        else
        {
            _pkiRoot = pkiRoot;
            _ownsPki = false;
        }
        Directory.CreateDirectory(_pkiRoot);
    }

    public async Task StartAsync()
    {
        var config = BuildConfig(Port, _pkiRoot);
        await config.Validate(ApplicationType.Server).ConfigureAwait(false);

        _app = new ApplicationInstance
        {
            ApplicationName = "Dc.IntegrationTest.UaServer",
            ApplicationType = ApplicationType.Server,
            ApplicationConfiguration = config
        };
        // 首次生成 self-signed 证书；隔离到 _pkiRoot 不污染开发者机器
        await _app.CheckApplicationInstanceCertificate(silent: true, minimumKeySize: 2048).ConfigureAwait(false);

        _server = new MinimalUaServer();
        await _app.Start(_server).ConfigureAwait(false);
    }

    public void Stop()
    {
        try { _server?.Stop(); } catch { }
        _server = null;
        _app = null;
    }

    public ValueTask DisposeAsync()
    {
        Stop();
        if (_ownsPki)
        {
            try { if (Directory.Exists(_pkiRoot)) Directory.Delete(_pkiRoot, recursive: true); } catch { }
        }
        return ValueTask.CompletedTask;
    }

    public static int FindFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public static ApplicationConfiguration BuildConfig(int port, string pkiRoot)
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
