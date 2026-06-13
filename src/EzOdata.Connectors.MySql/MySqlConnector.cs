using System.Data.Common;
using EzOdata.Connectors.Abstractions;
using EzOdata.Connectors.Abstractions.Sql;
using EzOdata.Core;
using EzOdata.Core.Query;
using EzOdata.Core.Schema;
using EzOdata.Core.Services;
using MySqlConnector;

namespace EzOdata.Connectors.MySql;

public static class MySqlConnectorFactory
{
    public static ConnectorDescriptor Create() => new(
        ConnectorTypes.MySql,
        new MySqlConnectionTester(),
        new MySqlIntrospector(),
        new MySqlQueryExecutor(),
        new MySqlWriteExecutor(),
        new MySqlDialect());

    internal static string BuildConnectionString(ConnectionSpec spec, int connectTimeoutSeconds = 5)
    {
        var builder = new MySqlConnectionStringBuilder
        {
            Server = spec.Host,
            Database = spec.Database,
            UserID = spec.Username,
            Password = spec.Password,
            ConnectionTimeout = (uint)connectTimeoutSeconds,
            ApplicationName = "ez-odata-api",
            SslMode = spec.Tls.Mode switch
            {
                "disable" => MySqlSslMode.None,
                "require" when spec.Tls.AllowInvalid => MySqlSslMode.Required,
                "require" => MySqlSslMode.VerifyFull,
                _ => MySqlSslMode.Preferred,
            },
        };
        if (spec.Port is { } port) builder.Port = (uint)port;
        return builder.ConnectionString;
    }

    internal static Exception Map(Exception exception)
    {
        if (exception is not MySqlException ex) return exception;

        return ex.ErrorCode switch
        {
            MySqlErrorCode.DuplicateKeyEntry => new ConnectorException(
                ErrorCodes.ConflictUniqueViolation, "Unique constraint violated.", inner: ex),
            MySqlErrorCode.RowIsReferenced or MySqlErrorCode.RowIsReferenced2
                or MySqlErrorCode.NoReferencedRow or MySqlErrorCode.NoReferencedRow2 => new ConnectorException(
                ErrorCodes.ConflictForeignKeyViolation, "Foreign key constraint violated.", inner: ex),
            MySqlErrorCode.ColumnCannotBeNull => new ConnectorException(
                ErrorCodes.ValidationNotNullViolation, "A required column cannot be null.", inner: ex),
            MySqlErrorCode.DataTooLong => new ConnectorException(
                ErrorCodes.ValidationValueTooLong, "Value too long for column.", inner: ex),
            MySqlErrorCode.AccessDenied => new ConnectorException(
                ErrorCodes.UpstreamPermissionDenied, "The service database account lacks permission.", inner: ex),
            MySqlErrorCode.QueryInterrupted => new ConnectorException(
                ErrorCodes.UpstreamTimeout, "Query cancelled by timeout.", isTransient: true, inner: ex),
            _ => new ConnectorException(ErrorCodes.InternalUnmapped, "Database error.", inner: ex),
        };
    }
}

public sealed class MySqlConnectionTester : IConnectionTester
{
    public async Task<ConnectionTestResult> TestAsync(ConnectionSpec spec, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));

        try
        {
            using var connection = new MySqlConnection(MySqlConnectorFactory.BuildConnectionString(spec));
            await connection.OpenAsync(timeout.Token);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT VERSION()";
            var version = (string?)await command.ExecuteScalarAsync(timeout.Token);
            return ConnectionTestResult.Success("MySQL " + version);
        }
        catch (MySqlException ex) when (ex.ErrorCode == MySqlErrorCode.AccessDenied)
        {
            return ConnectionTestResult.Failure(ConnectionTestCategory.AuthFailed,
                "Authentication failed for the supplied username/password.");
        }
        catch (MySqlException ex) when (ex.ErrorCode == MySqlErrorCode.UnknownDatabase)
        {
            return ConnectionTestResult.Failure(ConnectionTestCategory.DatabaseMissing,
                $"Database '{spec.Database}' does not exist on the server.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ConnectionTestResult.Failure(ConnectionTestCategory.Unreachable,
                "Connection attempt timed out after 10 seconds.");
        }
        catch (MySqlException)
        {
            return ConnectionTestResult.Failure(ConnectionTestCategory.Unreachable,
                "Could not reach the server. Check host, port, and network access.");
        }
    }
}

public sealed class MySqlQueryExecutor : AdoQueryExecutor
{
    public MySqlQueryExecutor() : base(new MySqlDialect()) { }

    protected override DbConnection CreateConnection(ConnectionSpec spec) =>
        new MySqlConnection(MySqlConnectorFactory.BuildConnectionString(spec));

    protected override Exception MapException(Exception exception) => MySqlConnectorFactory.Map(exception);
}

public sealed class MySqlWriteExecutor : AdoWriteExecutor
{
    public MySqlWriteExecutor() : base(new MySqlDialect()) { }

    protected override DbConnection CreateConnection(ConnectionSpec spec) =>
        new MySqlConnection(MySqlConnectorFactory.BuildConnectionString(spec));

    protected override Exception MapException(Exception exception) => MySqlConnectorFactory.Map(exception);

    /// <summary>MySQL has no RETURNING: the generated key is on the executed command (spec 04 §7.3).</summary>
    protected override long? GetLastInsertedId(DbCommand insertCommand) =>
        insertCommand is MySqlCommand { LastInsertedId: > 0 } mysql ? mysql.LastInsertedId : null;
}
