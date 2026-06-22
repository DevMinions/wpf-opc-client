using System.IO;
using Opc.Ua;

namespace Dc.Opc.Ua;

// 内部 → public 以便 App 启动时设置 AutoAcceptUntrustedCertificates 静态属性
public static class OpcUaApplicationConfig
{
    // 由 App 启动时从 appsettings.json "OpcUa:AutoAcceptUntrustedCertificates" 写入。
    // 默认 false — 产线必须显式信任服务器证书（把证书放进 pki/trusted/certs/）。
    // dev 环境若想跳过证书校验，appsettings.json 里设 true。
    public static bool AutoAcceptUntrustedCertificates { get; set; } = false;

    // 由 App 启动时从 appsettings.json "OpcUa:UseSecurity" 写入。默认 true（Phase 8b 安全基线，不可降级）。
    // dev 环境连只暴露 SecurityPolicy=None 的测试 server（如 Prosys Simulation Server）时设 false，
    // SelectEndpoint 会挑 None 端点——不交换证书，免去双向证书信任。产线保持 true。
    public static bool UseSecurity { get; set; } = true;

    // 1024 在现代 OPC UA 部署已不安全；2048 是当前主流 server 的最小密钥长度。
    public static ushort MinimumCertificateKeySize { get; set; } = 2048;

    public static ApplicationConfiguration Build(TimeSpan operationTimeout)
    {
        var pkiRoot = Path.Combine(AppContext.BaseDirectory, "pki");
        return new ApplicationConfiguration
        {
            ApplicationName = "DcCollector",
            ApplicationUri = $"urn:{Environment.MachineName}:DcCollector",
            ApplicationType = ApplicationType.Client,
            SecurityConfiguration = new SecurityConfiguration
            {
                AutoAcceptUntrustedCertificates = AutoAcceptUntrustedCertificates,
                MinimumCertificateKeySize = MinimumCertificateKeySize,
                ApplicationCertificate = new CertificateIdentifier
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = Path.Combine(pkiRoot, "own"),
                    SubjectName = $"CN=DcCollector, O=Dc, DC={Environment.MachineName}"
                },
                TrustedIssuerCertificates = new CertificateTrustList
                { StoreType = CertificateStoreType.Directory, StorePath = Path.Combine(pkiRoot, "issuer") },
                TrustedPeerCertificates = new CertificateTrustList
                { StoreType = CertificateStoreType.Directory, StorePath = Path.Combine(pkiRoot, "trusted") },
                RejectedCertificateStore = new CertificateTrustList
                { StoreType = CertificateStoreType.Directory, StorePath = Path.Combine(pkiRoot, "rejected") }
            },
            TransportQuotas = new TransportQuotas
            {
                OperationTimeout = (int)operationTimeout.TotalMilliseconds,
                MaxStringLength = 1048576,
                MaxByteStringLength = 1048576,
                MaxArrayLength = 65535,
                MaxMessageSize = 4194304,
                MaxBufferSize = 65535,
                ChannelLifetime = 300000,
                SecurityTokenLifetime = 3600000
            },
            ClientConfiguration = new ClientConfiguration { DefaultSessionTimeout = 60000 },
            CertificateValidator = new CertificateValidator(),
            TraceConfiguration = new TraceConfiguration()
        };
    }
}
