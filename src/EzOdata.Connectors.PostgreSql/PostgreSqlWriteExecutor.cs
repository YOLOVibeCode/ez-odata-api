using EzOdata.Connectors.Abstractions;
using EzOdata.Connectors.Abstractions.Sql;
using EzOdata.Core;
using EzOdata.Core.Query;
using EzOdata.Core.Schema;
using Npgsql;

namespace EzOdata.Connectors.PostgreSql;

/// <summary>
/// PostgreSQL writes (spec 04 §7.3): transactional single/bulk/deep inserts with
/// RETURNING, keyed updates/deletes with preconditions, error taxonomy mapping (§8).
/// </summary>
public sealed class PostgreSqlWriteExecutor : IWriteExecutor
{
    private readonly WriteSqlCompiler _compiler = new(new PostgreSqlDialect());

    public async Task<WriteResult> WriteAsync(WriteExecution execution, CancellationToken ct)
    {
        using var connection = new NpgsqlConnection(PostgreSqlConnectionStrings.Build(execution.Connection));
        await connection.OpenAsync(ct);
        using var transaction = connection.BeginTransaction(); // netstandard2.0: no async transactions

        try
        {
            var result = await ExecuteAsync(connection, transaction, execution, ct);
            transaction.Commit();
            return result;
        }
        catch (PostgresException ex)
        {
            transaction.Rollback();
            throw Map(ex);
        }
    }

    public async Task<IReadOnlyList<WriteResult>> WriteAtomicAsync(
        IReadOnlyList<WriteExecution> executions, CancellationToken ct)
    {
        if (executions.Count == 0) return [];

        using var connection = new NpgsqlConnection(PostgreSqlConnectionStrings.Build(executions[0].Connection));
        await connection.OpenAsync(ct);
        using var transaction = connection.BeginTransaction(); // netstandard2.0: no async transactions

        try
        {
            var results = new List<WriteResult>(executions.Count);
            foreach (var execution in executions)
            {
                results.Add(await ExecuteAsync(connection, transaction, execution, ct));
            }

            transaction.Commit();
            return results;
        }
        catch (PostgresException ex)
        {
            transaction.Rollback();
            throw Map(ex);
        }
    }

    private async Task<WriteResult> ExecuteAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        WriteExecution execution, CancellationToken ct)
    {
        var write = execution.Write;
        var table = execution.Schema.FindTable(write.Table)
            ?? throw new QueryValidationException(ErrorCodes.ValidationInvalidValue, $"Unknown table '{write.Table}'.");

        return write.Kind switch
        {
            WriteKind.Insert => await InsertAsync(connection, transaction, execution, table, ct),
            WriteKind.Update or WriteKind.Replace => await UpdateAsync(connection, transaction, execution, table, ct),
            WriteKind.Delete => await DeleteAsync(connection, transaction, execution, table, ct),
            _ => throw new NotSupportedQueryException($"Unsupported write kind {write.Kind}."),
        };
    }

    private async Task<WriteResult> InsertAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        WriteExecution execution, TableModel table, CancellationToken ct)
    {
        var rows = new List<Row>();

        foreach (var record in execution.Write.Records)
        {
            var inserted = await InsertOneAsync(connection, transaction, execution, table, record, ct);
            rows.Add(inserted);
        }

        // Row-filtered identities may only insert rows they would be able to see
        // (spec 08 §5.4); violation rolls the transaction back via the thrown exception.
        if (execution.Write.InsertVisibilityFilter is { } visibility && table.PrimaryKey.Count > 0)
        {
            foreach (var row in rows)
            {
                await EnsureRowVisibleAsync(connection, transaction, execution, table, visibility, row, ct);
            }
        }

        return new WriteResult(rows.Count, rows);
    }

    private async Task EnsureRowVisibleAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, WriteExecution execution,
        TableModel table, FilterNode visibility, Row row, CancellationToken ct)
    {
        FilterNode predicate = visibility;
        foreach (var key in table.PrimaryKey)
        {
            predicate = new LogicalNode(LogicalOp.And,
            [
                predicate,
                new ComparisonNode(new FieldRef(key), ComparisonOp.Eq, new ConstantValue(row[key])),
            ]);
        }

        var readCompiler = new SqlCompiler(new PostgreSqlDialect());
        var compiled = readCompiler.CompileCount(execution.Schema, new QueryRequest
        {
            ServiceName = execution.Write.ServiceName,
            Table = table.ExposedName,
            Filter = predicate,
        });

        using var command = Build(connection, transaction, compiled, execution.Options);
        var visible = Convert.ToInt64(await command.ExecuteScalarAsync(ct)) > 0;
        if (!visible)
        {
            throw new ConnectorException(ErrorCodes.ForbiddenRowFilter,
                "Inserted record does not satisfy the role's row filter.");
        }
    }

    private async Task<Row> InsertOneAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        WriteExecution execution, TableModel table, RecordPayload record, CancellationToken ct)
    {
        var compiled = _compiler.CompileInsert(execution.Schema, table, record, returning: true);

        using var command = Build(connection, transaction, compiled, execution.Options);
        using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            throw new ConnectorException(ErrorCodes.InternalUnmapped, "Insert returned no row.");
        }

        var row = ReadRow(reader);
        await reader.DisposeAsync();

        // Deep insert (spec 05 §5.1): children get the parent's key via the FK columns.
        foreach (var child in record.Children)
        {
            var childTable = execution.Schema.Tables.First(t =>
                t.ForeignKeys.Any(fk => fk.RefTable == table.ExposedName && fk.NavToMany == child.Key));
            var fk = childTable.ForeignKeys.First(f => f.RefTable == table.ExposedName && f.NavToMany == child.Key);

            foreach (var childRecord in child.Value)
            {
                var values = childRecord.Values.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
                for (var i = 0; i < fk.Columns.Count; i++)
                {
                    values[fk.Columns[i]] = row[fk.RefColumns[i]];
                }

                await InsertOneAsync(connection, transaction, execution, childTable,
                    childRecord with { Values = values }, ct);
            }
        }

        return row;
    }

    private async Task<WriteResult> UpdateAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        WriteExecution execution, TableModel table, CancellationToken ct)
    {
        var compiled = _compiler.CompileUpdate(execution.Schema, table, execution.Write, returning: true);

        using var command = Build(connection, transaction, compiled, execution.Options);
        using var reader = await command.ExecuteReaderAsync(ct);

        var rows = new List<Row>();
        while (await reader.ReadAsync(ct))
        {
            rows.Add(ReadRow(reader));
        }

        return new WriteResult(rows.Count, rows);
    }

    private async Task<WriteResult> DeleteAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        WriteExecution execution, TableModel table, CancellationToken ct)
    {
        var compiled = _compiler.CompileDelete(execution.Schema, table, execution.Write);

        using var command = Build(connection, transaction, compiled, execution.Options);
        var affected = await command.ExecuteNonQueryAsync(ct);
        return new WriteResult(affected, []);
    }

    private static Row ReadRow(NpgsqlDataReader reader)
    {
        var values = new KeyValuePair<string, object?>[reader.FieldCount];
        for (var i = 0; i < reader.FieldCount; i++)
        {
            values[i] = new KeyValuePair<string, object?>(
                reader.GetName(i), reader.IsDBNull(i) ? null : reader.GetValue(i));
        }

        return new Row(values);
    }

    private static NpgsqlCommand Build(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        CompiledQuery compiled, ExecutionOptions options)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = compiled.Sql;
        command.CommandTimeout = options.CommandTimeoutSeconds;
        foreach (var parameter in compiled.Parameters)
        {
            command.Parameters.AddWithValue(parameter.Name.TrimStart('@'), parameter.Value ?? DBNull.Value);
        }

        return command;
    }

    /// <summary>Provider error → shared taxonomy (spec 04 §8). Raw messages stay server-side.</summary>
    internal static ConnectorException Map(PostgresException ex) => ex.SqlState switch
    {
        "23505" => new ConnectorException(ErrorCodes.ConflictUniqueViolation,
            $"Unique constraint violated ({ex.ConstraintName ?? "unknown constraint"}).", inner: ex),
        "23503" => new ConnectorException(ErrorCodes.ConflictForeignKeyViolation,
            $"Foreign key constraint violated ({ex.ConstraintName ?? "unknown constraint"}).", inner: ex),
        "23502" => new ConnectorException(ErrorCodes.ValidationNotNullViolation,
            $"Column '{ex.ColumnName ?? "unknown"}' cannot be null.", inner: ex),
        "22001" => new ConnectorException(ErrorCodes.ValidationValueTooLong, "Value too long for column.", inner: ex),
        "22P02" or "22003" or "22007" => new ConnectorException(ErrorCodes.ValidationInvalidValue,
            "Value has an invalid format for its column type.", inner: ex),
        "42501" => new ConnectorException(ErrorCodes.UpstreamPermissionDenied,
            "The service database account lacks permission for this operation.", inner: ex),
        "57014" => new ConnectorException(ErrorCodes.UpstreamTimeout, "Query cancelled by timeout.", isTransient: true, inner: ex),
        _ => new ConnectorException(ErrorCodes.InternalUnmapped, "Database error.", inner: ex),
    };
}
