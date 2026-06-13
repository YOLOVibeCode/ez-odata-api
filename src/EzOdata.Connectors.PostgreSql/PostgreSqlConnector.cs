using EzOdata.Connectors.Abstractions;
using EzOdata.Core.Services;

namespace EzOdata.Connectors.PostgreSql;

public static class PostgreSqlConnector
{
    public static ConnectorDescriptor Create() => new(
        ConnectorTypes.PostgreSql,
        new PostgreSqlConnectionTester(),
        new PostgreSqlIntrospector(),
        new PostgreSqlQueryExecutor(),
        new PostgreSqlWriteExecutor(),
        new PostgreSqlDialect());
}
