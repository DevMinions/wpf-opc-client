using Dc.Opc.Abstractions;

namespace Dc.Opc.Da;

public sealed class OpcDaBrowserFactory : IOpcBrowserFactory
{
    public OpcProtocol Protocol => OpcProtocol.Da;
    public IOpcBrowser Create() => new OpcDaBrowser();
}
