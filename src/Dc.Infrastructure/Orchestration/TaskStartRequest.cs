using Dc.Opc.Abstractions;

namespace Dc.Infrastructure.Orchestration;

public sealed record TaskStartRequest(
    string TaskId,
    OpcProtocol Protocol,
    OpcConnectionOptions OpcOptions,
    string PublisherAddress,
    IReadOnlyCollection<TagDescriptor> Tags,
    TransformConfig? TransformConfig = null);
