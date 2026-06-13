namespace EzOdata.Connectors.Abstractions;

/// <summary>Lookup of registered connectors by connector type key (spec 04 §2).</summary>
public interface IConnectorRegistry
{
    /// <summary>Known connector type keys, e.g. "postgresql", "mysql", "sqlserver", "sqlite".</summary>
    IReadOnlyCollection<string> ConnectorTypes { get; }

    bool TryGet(string connectorType, out ConnectorDescriptor descriptor);
}
