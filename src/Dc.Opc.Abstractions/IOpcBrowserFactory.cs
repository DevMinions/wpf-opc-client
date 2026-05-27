namespace Dc.Opc.Abstractions;

public interface IOpcBrowserFactory
{
    OpcProtocol Protocol { get; }
    IOpcBrowser Create();
}
