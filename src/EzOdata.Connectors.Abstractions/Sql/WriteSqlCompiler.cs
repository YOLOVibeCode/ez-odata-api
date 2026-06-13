using System.Text;
using EzOdata.Core;
using EzOdata.Core.Query;
using EzOdata.Core.Schema;

namespace EzOdata.Connectors.Abstractions.Sql;

/// <summary>
/// Write-side SQL compilation (spec 04 §7.3), sharing the dialect + parameterization
/// rules with <see cref="SqlCompiler"/>. RETURNING-style key retrieval is dialect-owned.
/// </summary>
public sealed class WriteSqlCompiler
{
    private readonly ISqlDialect _dialect;
    private readonly SqlCompiler _readCompiler;

    public WriteSqlCompiler(ISqlDialect dialect)
    {
        _dialect = dialect;
        _readCompiler = new SqlCompiler(dialect);
    }

    public CompiledQuery CompileInsert(SchemaSnapshot schema, TableModel table, RecordPayload record, bool returning)
    {
        var columns = new List<ColumnModel>();
        foreach (var pair in record.Values)
        {
            var column = table.FindColumn(pair.Key)
                ?? throw new QueryValidationException(ErrorCodes.ValidationUnknownProperty,
                    $"Unknown property '{pair.Key}' on '{table.ExposedName}'.");
            columns.Add(column);
        }

        var parameters = new List<SqlParam>();
        var markers = new List<string>();
        foreach (var column in columns)
        {
            var marker = $"@p{parameters.Count}";
            parameters.Add(new SqlParam(marker, record.Values[column.ExposedName]));
            markers.Add(marker);
        }

        var sql = new StringBuilder("INSERT INTO ").Append(Qualified(table));
        if (columns.Count > 0)
        {
            sql.Append(" (")
               .Append(string.Join(", ", columns.Select(c => _dialect.QuoteIdentifier(c.DbName))));

            if (returning && _dialect.Returning == ReturningMode.OutputClause)
            {
                sql.Append(") ").Append(OutputClause("INSERTED", table)).Append(" VALUES (");
            }
            else
            {
                sql.Append(") VALUES (");
            }

            sql.Append(string.Join(", ", markers)).Append(')');
        }
        else
        {
            sql.Append(" DEFAULT VALUES");
        }

        if (returning && _dialect.Returning == ReturningMode.ReturningSuffix)
        {
            sql.Append(" RETURNING ").Append(ReturningList(table));
        }

        return new CompiledQuery(sql.ToString(), parameters, table.Columns);
    }

    public CompiledQuery CompileUpdate(SchemaSnapshot schema, TableModel table, WriteRequest write, bool returning)
    {
        var record = write.Records.Count == 1
            ? write.Records[0]
            : throw new QueryValidationException(ErrorCodes.ValidationInvalidValue, "Update requires exactly one record.");

        if (record.Values.Count == 0)
        {
            throw new QueryValidationException(ErrorCodes.ValidationInvalidValue, "Update payload contains no writable values.");
        }

        var parameters = new List<SqlParam>();
        var assignments = new List<string>();
        foreach (var pair in record.Values)
        {
            var column = table.FindColumn(pair.Key)
                ?? throw new QueryValidationException(ErrorCodes.ValidationUnknownProperty,
                    $"Unknown property '{pair.Key}'.");
            if (column.IsPrimaryKey)
            {
                throw new QueryValidationException(ErrorCodes.ValidationInvalidValue,
                    $"Key property '{pair.Key}' cannot be updated.");
            }

            var marker = $"@p{parameters.Count}";
            parameters.Add(new SqlParam(marker, pair.Value));
            assignments.Add($"{_dialect.QuoteIdentifier(column.DbName)} = {marker}");
        }

        var sql = new StringBuilder("UPDATE ").Append(Qualified(table))
            .Append(" SET ").Append(string.Join(", ", assignments));

        if (returning && _dialect.Returning == ReturningMode.OutputClause)
        {
            sql.Append(' ').Append(OutputClause("INSERTED", table));
        }

        AppendWhere(sql, parameters, schema, table, write);

        if (returning && _dialect.Returning == ReturningMode.ReturningSuffix)
        {
            sql.Append(" RETURNING ").Append(ReturningList(table));
        }

        return new CompiledQuery(sql.ToString(), parameters, table.Columns);
    }

    private string ReturningList(TableModel table) =>
        string.Join(", ", table.Columns.Select(c =>
            $"{_dialect.QuoteIdentifier(c.DbName)} AS {_dialect.QuoteIdentifier(c.ExposedName)}"));

    private string OutputClause(string source, TableModel table) =>
        "OUTPUT " + string.Join(", ", table.Columns.Select(c =>
            $"{source}.{_dialect.QuoteIdentifier(c.DbName)} AS {_dialect.QuoteIdentifier(c.ExposedName)}"));

    public CompiledQuery CompileDelete(SchemaSnapshot schema, TableModel table, WriteRequest write)
    {
        var parameters = new List<SqlParam>();
        var sql = new StringBuilder("DELETE FROM ").Append(Qualified(table));
        AppendWhere(sql, parameters, schema, table, write);
        return new CompiledQuery(sql.ToString(), parameters, []);
    }

    private void AppendWhere(
        StringBuilder sql, List<SqlParam> parameters,
        SchemaSnapshot schema, TableModel table, WriteRequest write)
    {
        var clauses = new List<string>();

        if (write.Key is { } key)
        {
            foreach (var pair in key.Values)
            {
                var column = table.FindColumn(pair.Key)
                    ?? throw new QueryValidationException(ErrorCodes.ValidationUnknownProperty,
                        $"Unknown key property '{pair.Key}'.");
                var marker = $"@p{parameters.Count}";
                parameters.Add(new SqlParam(marker, pair.Value));
                clauses.Add($"{_dialect.QuoteIdentifier(column.DbName)} = {marker}");
            }
        }

        if (write.Precondition is { } precondition)
        {
            // Reuse the read compiler's filter rendering via a SELECT compile, then splice
            // the WHERE fragment with re-numbered parameters.
            var probe = _readCompiler.CompileSelect(schema, new QueryRequest
            {
                ServiceName = write.ServiceName,
                Table = table.ExposedName,
                Filter = precondition,
                Select = table.PrimaryKey.Count > 0 ? table.PrimaryKey : [table.Columns[0].ExposedName],
            });

            var whereIndex = probe.Sql.IndexOf(" WHERE ", StringComparison.Ordinal);
            if (whereIndex >= 0)
            {
                var orderIndex = probe.Sql.IndexOf(" ORDER BY ", StringComparison.Ordinal);
                var end = orderIndex > whereIndex ? orderIndex : probe.Sql.Length;
                var fragment = probe.Sql.Substring(whereIndex + 7, end - whereIndex - 7);

                // The fragment references alias t0; UPDATE/DELETE have no alias.
                fragment = fragment.Replace("t0.", "");

                // Re-number @pN markers after the ones already emitted.
                foreach (var parameter in probe.Parameters)
                {
                    var newMarker = $"@p{parameters.Count}";
                    fragment = fragment.Replace(parameter.Name + ")", newMarker + ")")
                                       .Replace(parameter.Name + " ", newMarker + " ")
                                       .Replace(parameter.Name + ",", newMarker + ",");
                    if (fragment.EndsWith(parameter.Name, StringComparison.Ordinal))
                    {
                        fragment = fragment.Substring(0, fragment.Length - parameter.Name.Length) + newMarker;
                    }

                    parameters.Add(new SqlParam(newMarker, parameter.Value));
                }

                clauses.Add($"({fragment})");
            }
        }

        if (clauses.Count == 0)
        {
            // Defense in depth: an unkeyed, unfiltered write would touch every row.
            throw new QueryValidationException(ErrorCodes.ValidationInvalidValue,
                "Write operations require a key or filter predicate.");
        }

        sql.Append(" WHERE ").Append(string.Join(" AND ", clauses));
    }

    private string Qualified(TableModel table) =>
        string.IsNullOrEmpty(table.DbSchema)
            ? _dialect.QuoteIdentifier(table.DbName)
            : $"{_dialect.QuoteIdentifier(table.DbSchema)}.{_dialect.QuoteIdentifier(table.DbName)}";
}
