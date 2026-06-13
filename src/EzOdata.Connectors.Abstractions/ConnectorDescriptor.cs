namespace EzOdata.Connectors.Abstractions;

/// <summary>
/// Composition + capability discovery for one engine (spec 04 §2).
/// Not a god interface: protocol layers resolve the specific capability they need
/// and inject that; they never call through the descriptor.
/// Writer is null for read-only engines — read-only by type, not by guard (CON-7).
/// </summary>
public sealed record ConnectorDescriptor(
    string ConnectorType,
    IConnectionTester Tester,
    ISchemaIntrospector Introspector,
    IQueryExecutor Reader,
    IWriteExecutor? Writer,
    ISqlDialect Dialect);
