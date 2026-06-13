using System.Data.Common;
using EzOdata.Core;
using EzOdata.Core.Query;
using EzOdata.Core.Schema;

namespace EzOdata.Connectors.Abstractions.Sql;

/// <summary>
/// Shared ADO.NET read execution: connectors supply a connection factory, dialect,
/// and exception mapping; structure/parameterization stays in the shared compilers.
/// </summary>
public abstract class AdoQueryExecutor : IQueryExecutor
{
    private readonly SqlCompiler _compiler;

    protected AdoQueryExecutor(ISqlDialect dialect) => _compiler = new SqlCompiler(dialect);

    protected abstract DbConnection CreateConnection(ConnectionSpec spec);

    /// <summary>Provider exception → taxonomy (spec 04 §8); rethrow anything unmapped.</summary>
    protected abstract Exception MapException(Exception exception);

    public async Task<QueryResult> QueryAsync(QueryExecution execution, CancellationToken ct)
    {
        var compiled = execution.Query.Apply is not null
            ? _compiler.CompileApply(execution.Schema, execution.Query)
            : _compiler.CompileSelect(execution.Schema, execution.Query);
        var limit = execution.Query.Apply is not null ? null : execution.Query.Top;

        try
        {
            using var connection = CreateConnection(execution.Connection);
            await connection.OpenAsync(ct);
            using var command = Build(connection, null, compiled, execution.Options);
            using var reader = await command.ExecuteReaderAsync(ct);

            var rows = new List<Row>();
            while (await reader.ReadAsync(ct))
            {
                rows.Add(ReadRow(reader));
                if (limit is { } l && rows.Count > l) break;
            }

            var hasMore = limit is { } lim && rows.Count > lim;
            if (hasMore) rows.RemoveAt(rows.Count - 1);
            return new QueryResult(rows, hasMore, null);
        }
        catch (DbException ex)
        {
            throw MapException(ex);
        }
    }

    public async Task<long> CountAsync(QueryExecution execution, CancellationToken ct)
    {
        var compiled = _compiler.CompileCount(execution.Schema, execution.Query);

        try
        {
            using var connection = CreateConnection(execution.Connection);
            await connection.OpenAsync(ct);
            using var command = Build(connection, null, compiled, execution.Options);
            return Convert.ToInt64(await command.ExecuteScalarAsync(ct));
        }
        catch (DbException ex)
        {
            throw MapException(ex);
        }
    }

    public static Row ReadRow(DbDataReader reader)
    {
        var values = new KeyValuePair<string, object?>[reader.FieldCount];
        for (var i = 0; i < reader.FieldCount; i++)
        {
            values[i] = new KeyValuePair<string, object?>(
                reader.GetName(i), reader.IsDBNull(i) ? null : reader.GetValue(i));
        }

        return new Row(values);
    }

    public static DbCommand Build(
        DbConnection connection, DbTransaction? transaction,
        CompiledQuery compiled, ExecutionOptions options)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = compiled.Sql;
        command.CommandTimeout = options.CommandTimeoutSeconds;
        foreach (var parameter in compiled.Parameters)
        {
            var dbParameter = command.CreateParameter();
            dbParameter.ParameterName = parameter.Name.TrimStart('@');
            dbParameter.Value = parameter.Value ?? DBNull.Value;
            command.Parameters.Add(dbParameter);
        }

        return command;
    }
}

/// <summary>
/// Shared ADO.NET write execution with per-dialect returning strategies (spec 04 §7.3):
/// ReturningSuffix/OutputClause read inline; ReturningMode.None uses the
/// <see cref="RetrieveInsertedRowAsync"/> hook (e.g. MySQL LAST_INSERT_ID).
/// </summary>
public abstract class AdoWriteExecutor : IWriteExecutor
{
    private readonly ISqlDialect _dialect;
    private readonly WriteSqlCompiler _compiler;
    private readonly SqlCompiler _readCompiler;

    protected AdoWriteExecutor(ISqlDialect dialect)
    {
        _dialect = dialect;
        _compiler = new WriteSqlCompiler(dialect);
        _readCompiler = new SqlCompiler(dialect);
    }

    protected abstract DbConnection CreateConnection(ConnectionSpec spec);

    protected abstract Exception MapException(Exception exception);

    /// <summary>
    /// Engines without RETURNING expose the generated identity here (e.g. MySQL
    /// <c>MySqlCommand.LastInsertedId</c>), read off the just-executed insert command.
    /// </summary>
    protected virtual long? GetLastInsertedId(DbCommand insertCommand) => null;

    public async Task<WriteResult> WriteAsync(WriteExecution execution, CancellationToken ct)
    {
        var results = await WriteAtomicAsync([execution], ct);
        return results[0];
    }

    public async Task<IReadOnlyList<WriteResult>> WriteAtomicAsync(
        IReadOnlyList<WriteExecution> executions, CancellationToken ct)
    {
        if (executions.Count == 0) return [];

        try
        {
            using var connection = CreateConnection(executions[0].Connection);
            await connection.OpenAsync(ct);
            using var transaction = connection.BeginTransaction();

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
            catch
            {
                transaction.Rollback();
                throw;
            }
        }
        catch (DbException ex)
        {
            throw MapException(ex);
        }
    }

    private async Task<WriteResult> ExecuteAsync(
        DbConnection connection, DbTransaction transaction, WriteExecution execution, CancellationToken ct)
    {
        var write = execution.Write;
        var table = execution.Schema.FindTable(write.Table)
            ?? throw new QueryValidationException(ErrorCodes.ValidationInvalidValue, $"Unknown table '{write.Table}'.");

        switch (write.Kind)
        {
            case WriteKind.Insert:
            {
                var rows = new List<Row>();
                foreach (var record in write.Records)
                {
                    rows.Add(await InsertOneAsync(connection, transaction, execution, table, record, ct));
                }

                if (write.InsertVisibilityFilter is { } visibility && table.PrimaryKey.Count > 0)
                {
                    foreach (var row in rows)
                    {
                        await EnsureVisibleAsync(connection, transaction, execution, table, visibility, row, ct);
                    }
                }

                return new WriteResult(rows.Count, rows);
            }

            case WriteKind.Update:
            case WriteKind.Replace:
            {
                var compiled = _compiler.CompileUpdate(execution.Schema, table, write,
                    returning: _dialect.Returning != ReturningMode.None);
                using var command = AdoQueryExecutor.Build(connection, transaction, compiled, execution.Options);

                if (_dialect.Returning == ReturningMode.None)
                {
                    var affected = await command.ExecuteNonQueryAsync(ct);
                    if (affected == 0) return new WriteResult(0, []);

                    var row = write.Key is { } key
                        ? await ReadByKeyAsync(connection, transaction, execution, table, key, ct)
                        : null;
                    return new WriteResult(affected, row is null ? [] : [row]);
                }

                using var reader = await command.ExecuteReaderAsync(ct);
                var rows = new List<Row>();
                while (await reader.ReadAsync(ct)) rows.Add(AdoQueryExecutor.ReadRow(reader));
                return new WriteResult(rows.Count, rows);
            }

            case WriteKind.Delete:
            {
                var compiled = _compiler.CompileDelete(execution.Schema, table, write);
                using var command = AdoQueryExecutor.Build(connection, transaction, compiled, execution.Options);
                var affected = await command.ExecuteNonQueryAsync(ct);
                return new WriteResult(affected, []);
            }

            default:
                throw new NotSupportedQueryException($"Unsupported write kind {write.Kind}.");
        }
    }

    private async Task<Row> InsertOneAsync(
        DbConnection connection, DbTransaction transaction, WriteExecution execution,
        TableModel table, RecordPayload record, CancellationToken ct)
    {
        var compiled = _compiler.CompileInsert(execution.Schema, table, record,
            returning: _dialect.Returning != ReturningMode.None);
        using var command = AdoQueryExecutor.Build(connection, transaction, compiled, execution.Options);

        Row row;
        if (_dialect.Returning == ReturningMode.None)
        {
            await command.ExecuteNonQueryAsync(ct);
            row = await ReadBackAsync(connection, transaction, execution, table, record, command, ct)
                  ?? new Row(record.Values);
        }
        else
        {
            using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
            {
                throw new ConnectorException(ErrorCodes.InternalUnmapped, "Insert returned no row.");
            }

            row = AdoQueryExecutor.ReadRow(reader);
        }

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

    /// <summary>Read back the inserted row for no-RETURNING engines: by generated identity or supplied PK.</summary>
    private async Task<Row?> ReadBackAsync(
        DbConnection connection, DbTransaction transaction, WriteExecution execution,
        TableModel table, RecordPayload record, DbCommand insertCommand, CancellationToken ct)
    {
        var autoColumn = table.Columns.FirstOrDefault(c => c.IsAutoGenerated && c.IsPrimaryKey);
        if (autoColumn is not null && !record.Values.ContainsKey(autoColumn.ExposedName)
            && GetLastInsertedId(insertCommand) is { } lastId)
        {
            return await ReadByKeyAsync(connection, transaction, execution, table,
                new KeyPredicate(new Dictionary<string, object?> { [autoColumn.ExposedName] = lastId }), ct);
        }

        if (table.PrimaryKey.Count > 0 && table.PrimaryKey.All(record.Values.ContainsKey))
        {
            var keyValues = table.PrimaryKey.ToDictionary(k => k, k => record.Values[k], StringComparer.Ordinal);
            return await ReadByKeyAsync(connection, transaction, execution, table, new KeyPredicate(keyValues), ct);
        }

        return null;
    }

    private async Task EnsureVisibleAsync(
        DbConnection connection, DbTransaction transaction, WriteExecution execution,
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

        var compiled = _readCompiler.CompileCount(execution.Schema, new QueryRequest
        {
            ServiceName = execution.Write.ServiceName,
            Table = table.ExposedName,
            Filter = predicate,
        });

        using var command = AdoQueryExecutor.Build(connection, transaction, compiled, execution.Options);
        if (Convert.ToInt64(await command.ExecuteScalarAsync(ct)) == 0)
        {
            throw new ConnectorException(ErrorCodes.ForbiddenRowFilter,
                "Inserted record does not satisfy the role's row filter.");
        }
    }

    private async Task<Row?> ReadByKeyAsync(
        DbConnection connection, DbTransaction transaction, WriteExecution execution,
        TableModel table, KeyPredicate key, CancellationToken ct)
    {
        FilterNode? filter = null;
        foreach (var pair in key.Values)
        {
            var cmp = new ComparisonNode(new FieldRef(pair.Key), ComparisonOp.Eq, new ConstantValue(pair.Value));
            filter = filter is null ? cmp : new LogicalNode(LogicalOp.And, [filter, cmp]);
        }

        var compiled = _readCompiler.CompileSelect(execution.Schema, new QueryRequest
        {
            ServiceName = execution.Write.ServiceName,
            Table = table.ExposedName,
            Filter = filter,
            Top = 1,
        });

        using var command = AdoQueryExecutor.Build(connection, transaction, compiled, execution.Options);
        using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? AdoQueryExecutor.ReadRow(reader) : null;
    }
}
