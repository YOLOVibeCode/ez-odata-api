using EzOdata.Connectors.Abstractions;
using Npgsql;

namespace EzOdata.Connectors.PostgreSql;

public sealed class PostgreSqlConnectionTester : IConnectionTester
{
    public async Task<ConnectionTestResult> TestAsync(ConnectionSpec spec, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10)); // CON-5

        try
        {
            var connectionString = PostgreSqlConnectionStrings.Build(spec);
            using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(timeout.Token);

            using var command = connection.CreateCommand();
            command.CommandText = "SELECT version()";
            var version = (string?)await command.ExecuteScalarAsync(timeout.Token);

            return ConnectionTestResult.Success(version);
        }
        catch (PostgresException ex) when (ex.SqlState == "28P01" || ex.SqlState == "28000")
        {
            return ConnectionTestResult.Failure(ConnectionTestCategory.AuthFailed,
                "Authentication failed for the supplied username/password.");
        }
        catch (PostgresException ex) when (ex.SqlState == "3D000")
        {
            return ConnectionTestResult.Failure(ConnectionTestCategory.DatabaseMissing,
                $"Database '{spec.Database}' does not exist on the server.");
        }
        catch (PostgresException ex)
        {
            return ConnectionTestResult.Failure(ConnectionTestCategory.Other,
                $"Server rejected the connection ({ex.SqlState}).");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ConnectionTestResult.Failure(ConnectionTestCategory.Unreachable,
                "Connection attempt timed out after 10 seconds.");
        }
        catch (NpgsqlException ex) when (ex.InnerException is System.Security.Authentication.AuthenticationException)
        {
            return ConnectionTestResult.Failure(ConnectionTestCategory.TlsError,
                "TLS negotiation failed. Check the TLS mode and server certificate.");
        }
        catch (NpgsqlException)
        {
            return ConnectionTestResult.Failure(ConnectionTestCategory.Unreachable,
                "Could not reach the server. Check host, port, and network access.");
        }
    }
}
