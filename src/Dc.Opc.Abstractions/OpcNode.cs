namespace Dc.Opc.Abstractions;

public enum OpcNodeKind { Folder, Item }

public sealed record OpcNode(string Id, string DisplayName, OpcNodeKind Kind, bool HasChildren);
