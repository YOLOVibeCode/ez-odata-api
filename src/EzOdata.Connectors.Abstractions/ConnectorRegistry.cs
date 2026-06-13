namespace EzOdata.Connectors.Abstractions;

public sealed class ConnectorRegistry : IConnectorRegistry
{
    private readonly Dictionary<string, ConnectorDescriptor> _byType;

    public ConnectorRegistry(IEnumerable<ConnectorDescriptor> descriptors)
    {
        _byType = descriptors.ToDictionary(d => d.ConnectorType, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyCollection<string> ConnectorTypes => _byType.Keys;

    public bool TryGet(string connectorType, out ConnectorDescriptor descriptor) =>
        _byType.TryGetValue(connectorType, out descriptor!);
}
