using System.Data.Common;
using EzOdata.Connectors.Abstractions;
using EzOdata.Connectors.Abstractions.Sql;
using EzOdata.Core;
using EzOdata.Core.Query;
using EzOdata.Core.Services;
using Microsoft.Data.SqlClient;

namespace EzOdata.Connectors.SqlServer;

public static class SqlServerConnector
{
    public static ConnectorDescriptor Create() => new(
        ConnectorTypes.SqlServer,
        new SqlServerConnectionTester(),
        new SqlServerIntrospector(),
        new SqlServerQueryExecutor(),
        new SqlServerWriteExecutor(),
        new SqlServerDialect());

    internal static string BuildConnectionString(ConnectionSpec spec, int connectTimeoutSeconds = 5)
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = spec.Port is { } port ? $"{spec.Host},{port}" : spec.Host,
            InitialCatalog = spec.Database,
            UserID = spec.Username,
            Password = spec.Password,
            ConnectTimeout = connectTimeoutSeconds,
            ApplicationName = "ez-odata-api",
            Encrypt = spec.Tls.Mode != "disable",
            TrustServerCertificate = spec.Tls.AllowInvalid,
        };
        return builder.ConnectionString;
    }

    internal static Exception Map(Exception exception)
    {
        if (exception is not SqlException ex) return exception;

        return ex.Number switch
        {
            2627 or 2601 => new ConnectorException(ErrorCodes.ConflictUniqueViolation,
                "Unique constraint violated.", inner: ex),
            547 => new ConnectorException(ErrorCodes.ConflictForeignKeyViolation,
                "Foreign key constraint violated.", inner: ex),
            515 => new ConnectorException(ErrorCodes.ValidationNotNullViolation,
                "A required column cannot be null.", inner: ex),
            2628 or 8152 => new ConnectorException(ErrorCodes.ValidationValueTooLong,
                "Value too long for column.", inner: ex),
            229 or 230 => new ConnectorException(ErrorCodes.UpstreamPermissionDenied,
                "The service database account lacks permission.", inner: ex),
            -2 => new ConnectorException(ErrorCodes.UpstreamTimeout,
                "Query cancelled by timeout.", isTransient: true, inner: ex),
            _ => new ConnectorException(ErrorCodes.InternalUnmapped, "Database error.", inner: ex),
        };
    }
}

public sealed class SqlServerConnectionTester : IConnectionTester
{
    public async Task<ConnectionTestResult> TestAsync(ConnectionSpec spec, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));

        try
        {
            using var connection = new SqlConnection(SqlServerConnector.BuildConnectionString(spec));
            await connection.OpenAsync(timeout.Token);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT @@VERSION";
            var version = (string?)await command.ExecuteScalarAsync(timeout.Token);
            return ConnectionTestResult.Success(version?.Split('\n')[0].Trim());
        }
        catch (SqlException ex) when (ex.Number == 18456)
        {
            return ConnectionTestResult.Failure(ConnectionTestCategory.AuthFailed,
                "Authentication failed for the supplied username/password.");
        }
        catch (SqlException ex) when (ex.Number == 4060)
        {
            return ConnectionTestResult.Failure(ConnectionTestCategory.DatabaseMissing,
                $"Database '{spec.Database}' does not exist or is not accessible.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ConnectionTestResult.Failure(ConnectionTestCategory.Unreachable,
                "Connection attempt timed out after 10 seconds.");
        }
        catch (SqlException)
        {
            return ConnectionTestResult.Failure(ConnectionTestCategory.Unreachable,
                "Could not reach the server. Check host, port, and network access.");
        }
    }
}

public sealed class SqlServerQueryExecutor : AdoQueryExecutor
{
    public SqlServerQueryExecutor() : base(new SqlServerDialect()) { }

    protected override DbConnection CreateConnection(ConnectionSpec spec) =>
        new SqlConnection(SqlServerConnector.BuildConnectionString(spec));

    protected override Exception MapException(Exception exception) => SqlServerConnector.Map(exception);
}

public sealed class SqlServerWriteExecutor : AdoWriteExecutor
{
    public SqlServerWriteExecutor() : base(new SqlServerDialect()) { }

    protected override DbConnection CreateConnection(ConnectionSpec spec) =>
        new SqlConnection(SqlServerConnector.BuildConnectionString(spec));

    protected override Exception MapException(Exception exception) => SqlServerConnector.Map(exception);
}
