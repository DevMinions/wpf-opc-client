using Dc.Opc.Abstractions;

namespace Dc.Opc.Ae;

public sealed class OpcAeBrowserFactory : IOpcBrowserFactory
{
    public OpcProtocol Protocol => OpcProtocol.Ae;
    public IOpcBrowser Create() => new OpcAeBrowser();
}
