using Dc.Opc.Abstractions;

namespace Dc.Opc.Ua;

public sealed class OpcUaBrowserFactory : IOpcBrowserFactory
{
    public OpcProtocol Protocol => OpcProtocol.Ua;
    public IOpcBrowser Create() => new OpcUaBrowser();
}
