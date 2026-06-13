using System.Data.Common;
using EzOdata.Connectors.Abstractions;
using EzOdata.Connectors.Abstractions.Sql;
using EzOdata.Core;
using EzOdata.Core.Query;
using EzOdata.Core.Services;
using Microsoft.Data.Sqlite;

namespace EzOdata.Connectors.Sqlite;

public static class SqliteConnector
{
    public static ConnectorDescriptor Create() => new(
        ConnectorTypes.Sqlite,
        new SqliteConnectionTester(),
        new SqliteIntrospector(),
        new SqliteQueryExecutor(),
        new SqliteWriteExecutor(),
        new SqliteDialect());

    internal static string BuildConnectionString(ConnectionSpec spec) => new SqliteConnectionStringBuilder
    {
        DataSource = spec.FilePath ?? throw new QueryValidationException(
            ErrorCodes.ValidationInvalidValue, "SQLite requires a filePath."),
        Mode = spec.ReadOnlyFile ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite,
        ForeignKeys = true,
    }.ToString();

    internal static Exception Map(Exception exception)
    {
        if (exception is not SqliteException ex) return exception;

        return ex.SqliteExtendedErrorCode switch
        {
            2067 or 1555 => new ConnectorException(ErrorCodes.ConflictUniqueViolation,
                "Unique constraint violated.", inner: ex),
            787 => new ConnectorException(ErrorCodes.ConflictForeignKeyViolation,
                "Foreign key constraint violated.", inner: ex),
            1299 => new ConnectorException(ErrorCodes.ValidationNotNullViolation,
                "A required column cannot be null.", inner: ex),
            _ when ex.SqliteErrorCode == 19 => new ConnectorException(ErrorCodes.ValidationInvalidValue,
                "Constraint violated.", inner: ex),
            14 => new ConnectorException(ErrorCodes.UpstreamUnavailable,
                "SQLite database file could not be opened.", isTransient: false, inner: ex),
            _ => new ConnectorException(ErrorCodes.InternalUnmapped, "Database error.", inner: ex),
        };
    }
}

public sealed class SqliteConnectionTester : IConnectionTester
{
    public async Task<ConnectionTestResult> TestAsync(ConnectionSpec spec, CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(spec.FilePath))
            {
                return ConnectionTestResult.Failure(ConnectionTestCategory.Other, "filePath is required for SQLite.");
            }

            using var connection = new SqliteConnection(SqliteConnector.BuildConnectionString(spec));
            await connection.OpenAsync(ct);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT sqlite_version()";
            var version = (string?)await command.ExecuteScalarAsync(ct);
            return ConnectionTestResult.Success("SQLite " + version);
        }
        catch (SqliteException ex)
        {
            return ConnectionTestResult.Failure(ConnectionTestCategory.Unreachable,
                $"Could not open the database file ({ex.SqliteErrorCode}).");
        }
    }
}

public sealed class SqliteQueryExecutor : AdoQueryExecutor
{
    public SqliteQueryExecutor() : base(new SqliteDialect()) { }

    protected override DbConnection CreateConnection(ConnectionSpec spec) =>
        new SqliteConnection(SqliteConnector.BuildConnectionString(spec));

    protected override Exception MapException(Exception exception) => SqliteConnector.Map(exception);
}

public sealed class SqliteWriteExecutor : AdoWriteExecutor
{
    public SqliteWriteExecutor() : base(new SqliteDialect()) { }

    protected override DbConnection CreateConnection(ConnectionSpec spec) =>
        new SqliteConnection(SqliteConnector.BuildConnectionString(spec));

    protected override Exception MapException(Exception exception) => SqliteConnector.Map(exception);
}
