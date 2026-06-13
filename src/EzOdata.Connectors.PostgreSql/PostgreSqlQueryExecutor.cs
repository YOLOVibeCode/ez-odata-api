using EzOdata.Connectors.Abstractions;
using EzOdata.Connectors.Abstractions.Sql;
using EzOdata.Core.Query;
using Npgsql;

namespace EzOdata.Connectors.PostgreSql;

public sealed class PostgreSqlQueryExecutor : IQueryExecutor
{
    private readonly SqlCompiler _compiler = new(new PostgreSqlDialect());

    public async Task<QueryResult> QueryAsync(QueryExecution execution, CancellationToken ct)
    {
        var compiled = execution.Query.Apply is not null
            ? _compiler.CompileApply(execution.Schema, execution.Query)
            : _compiler.CompileSelect(execution.Schema, execution.Query);
        var limit = execution.Query.Apply is not null ? null : execution.Query.Top;

        using var connection = await OpenAsync(execution, ct);
        using var command = Build(connection, compiled, execution.Options);
        using var reader = await command.ExecuteReaderAsync(ct);

        var rows = new List<Row>();
        while (await reader.ReadAsync(ct))
        {
            var values = new KeyValuePair<string, object?>[reader.FieldCount];
            for (var i = 0; i < reader.FieldCount; i++)
            {
                values[i] = new KeyValuePair<string, object?>(
                    reader.GetName(i),
                    reader.IsDBNull(i) ? null : reader.GetValue(i));
            }

            rows.Add(new Row(values));
            if (limit is { } l && rows.Count > l) break;
        }

        var hasMore = limit is { } lim && rows.Count > lim;
        if (hasMore) rows.RemoveAt(rows.Count - 1);

        return new QueryResult(rows, hasMore, NextCursor: null);
    }

    public async Task<long> CountAsync(QueryExecution execution, CancellationToken ct)
    {
        var compiled = _compiler.CompileCount(execution.Schema, execution.Query);

        using var connection = await OpenAsync(execution, ct);
        using var command = Build(connection, compiled, execution.Options);
        var result = await command.ExecuteScalarAsync(ct);
        return Convert.ToInt64(result);
    }

    private static async Task<NpgsqlConnection> OpenAsync(QueryExecution execution, CancellationToken ct)
    {
        var connection = new NpgsqlConnection(PostgreSqlConnectionStrings.Build(execution.Connection));
        await connection.OpenAsync(ct);
        return connection;
    }

    private static NpgsqlCommand Build(NpgsqlConnection connection, CompiledQuery compiled, ExecutionOptions options)
    {
        var command = connection.CreateCommand();
        command.CommandText = compiled.Sql;
        command.CommandTimeout = options.CommandTimeoutSeconds;
        foreach (var parameter in compiled.Parameters)
        {
            command.Parameters.AddWithValue(parameter.Name.TrimStart('@'), parameter.Value ?? DBNull.Value);
        }

        return command;
    }
}
