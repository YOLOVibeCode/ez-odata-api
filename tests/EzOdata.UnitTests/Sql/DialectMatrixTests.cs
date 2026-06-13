using EzOdata.Connectors.Abstractions;
using EzOdata.Connectors.Abstractions.Sql;
using EzOdata.Connectors.MySql;
using EzOdata.Connectors.Sqlite;
using EzOdata.Connectors.SqlServer;
using EzOdata.Core.Query;
using EzOdata.Core.Schema;
using Xunit;

namespace EzOdata.UnitTests.Sql;

/// <summary>
/// The spec 04 §7.2 translation matrix for the non-PostgreSQL engines: each row of the
/// dialect table is asserted against compiled SQL (red-first per engine, spec 13 §0.2).
/// </summary>
public class DialectMatrixTests
{
    private static readonly SchemaSnapshot Schema = new()
    {
        Engine = "any",
        Tables =
        [
            new TableModel
            {
                DbSchema = "", DbName = "orders", ExposedName = "orders",
                PrimaryKey = ["id"],
                Columns =
                [
                    new ColumnModel { DbName = "id", ExposedName = "id", DbType = "int", EdmType = "Edm.Int64", IsPrimaryKey = true },
                    new ColumnModel { DbName = "status", ExposedName = "status", DbType = "text", EdmType = "Edm.String" },
                    new ColumnModel { DbName = "total", ExposedName = "total", DbType = "decimal", EdmType = "Edm.Decimal" },
                    new ColumnModel { DbName = "created_at", ExposedName = "created_at", DbType = "datetime", EdmType = "Edm.DateTimeOffset" },
                ],
            },
        ],
    };

    private static QueryRequest Query() => new() { ServiceName = "s", Table = "orders" };

    // ---- MySQL ----

    [Fact]
    public void Mysql_quoting_and_pagination()
    {
        var compiled = new SqlCompiler(new MySqlDialect()).CompileSelect(Schema, Query() with { Top = 10, Skip = 5 });
        Assert.Contains("`orders`", compiled.Sql);
        Assert.EndsWith("LIMIT 11 OFFSET 5", compiled.Sql);
    }

    [Fact]
    public void Mysql_functions()
    {
        var dialect = new MySqlDialect();
        Assert.Equal("CHAR_LENGTH(`c`)", dialect.MapFunction(FilterFunction.Length, ["`c`"]));
        Assert.Equal("(LOCATE(@p0, `c`) - 1)", dialect.MapFunction(FilterFunction.IndexOf, ["`c`", "@p0"]));
        Assert.Equal("YEAR(`c`)", dialect.MapFunction(FilterFunction.Year, ["`c`"]));
        Assert.Equal("CONCAT(`a`, `b`)", dialect.MapFunction(FilterFunction.Concat, ["`a`", "`b`"]));
        Assert.Contains("LIKE", dialect.MapFunction(FilterFunction.Contains, ["`c`", "@p0"]));
        Assert.Equal(ReturningMode.None, dialect.Returning);
    }

    [Fact]
    public void Mysql_insert_has_no_returning()
    {
        var compiler = new WriteSqlCompiler(new MySqlDialect());
        var compiled = compiler.CompileInsert(Schema, Schema.Tables[0],
            new RecordPayload { Values = new Dictionary<string, object?> { ["status"] = "open" } },
            returning: false);
        Assert.Equal("INSERT INTO `orders` (`status`) VALUES (@p0)", compiled.Sql);
    }

    // ---- SQL Server ----

    [Fact]
    public void SqlServer_quoting_and_offset_fetch()
    {
        var compiled = new SqlCompiler(new SqlServerDialect()).CompileSelect(Schema, Query() with { Top = 10, Skip = 5 });
        Assert.Contains("[orders]", compiled.Sql);
        Assert.Contains("ORDER BY", compiled.Sql); // OFFSET..FETCH requires it
        Assert.EndsWith("OFFSET 5 ROWS FETCH NEXT 11 ROWS ONLY", compiled.Sql);
    }

    [Fact]
    public void SqlServer_top_without_skip_still_uses_offset_fetch()
    {
        var compiled = new SqlCompiler(new SqlServerDialect()).CompileSelect(Schema, Query() with { Top = 3 });
        Assert.EndsWith("OFFSET 0 ROWS FETCH NEXT 4 ROWS ONLY", compiled.Sql);
    }

    [Fact]
    public void SqlServer_functions()
    {
        var dialect = new SqlServerDialect();
        Assert.Equal("LEN([c])", dialect.MapFunction(FilterFunction.Length, ["[c]"]));
        Assert.Equal("(CHARINDEX(@p0, [c]) - 1)", dialect.MapFunction(FilterFunction.IndexOf, ["[c]", "@p0"]));
        Assert.Equal("DATEPART(year, [c])", dialect.MapFunction(FilterFunction.Year, ["[c]"]));
        Assert.Equal("CEILING([c])", dialect.MapFunction(FilterFunction.Ceiling, ["[c]"]));
        Assert.Equal(ReturningMode.OutputClause, dialect.Returning);
    }

    [Fact]
    public void SqlServer_insert_uses_output_inserted()
    {
        var compiler = new WriteSqlCompiler(new SqlServerDialect());
        var compiled = compiler.CompileInsert(Schema, Schema.Tables[0],
            new RecordPayload { Values = new Dictionary<string, object?> { ["status"] = "open" } },
            returning: true);
        Assert.Contains("OUTPUT INSERTED.[id] AS [id]", compiled.Sql);
        Assert.Contains(") OUTPUT", compiled.Sql);
        Assert.Contains("VALUES (@p0)", compiled.Sql);
        Assert.DoesNotContain("RETURNING", compiled.Sql);
    }

    // ---- SQLite ----

    [Fact]
    public void Sqlite_quoting_pagination_and_returning()
    {
        var compiled = new SqlCompiler(new SqliteDialect()).CompileSelect(Schema, Query() with { Skip = 5 });
        Assert.Contains("\"orders\"", compiled.Sql);
        Assert.EndsWith("LIMIT -1 OFFSET 5", compiled.Sql); // SQLite needs LIMIT before OFFSET

        var insert = new WriteSqlCompiler(new SqliteDialect()).CompileInsert(Schema, Schema.Tables[0],
            new RecordPayload { Values = new Dictionary<string, object?> { ["status"] = "x" } }, returning: true);
        Assert.Contains("RETURNING", insert.Sql);
    }

    [Fact]
    public void Sqlite_functions()
    {
        var dialect = new SqliteDialect();
        Assert.Equal("CAST(strftime('%Y', \"c\") AS INTEGER)", dialect.MapFunction(FilterFunction.Year, ["\"c\""]));
        Assert.Equal("(instr(\"c\", @p0) - 1)", dialect.MapFunction(FilterFunction.IndexOf, ["\"c\"", "@p0"]));
        Assert.Contains("CASE WHEN", dialect.MapFunction(FilterFunction.Floor, ["\"c\""])); // emulated
        Assert.Equal(ReturningMode.ReturningSuffix, dialect.Returning);
    }

    [Fact]
    public void Sqlite_type_mapping_by_declared_type()
    {
        Assert.Equal(("Edm.Int64", false), SqliteIntrospector.MapType("INTEGER"));
        Assert.Equal(("Edm.String", false), SqliteIntrospector.MapType("VARCHAR(100)"));
        Assert.Equal(("Edm.Boolean", false), SqliteIntrospector.MapType("BOOLEAN"));
        Assert.Equal(("Edm.DateTimeOffset", false), SqliteIntrospector.MapType("DATETIME"));
        Assert.Equal(("Edm.Decimal", false), SqliteIntrospector.MapType("DECIMAL(10,2)"));
        Assert.Equal(("Edm.Binary", false), SqliteIntrospector.MapType("BLOB"));
    }

    [Fact]
    public void Mysql_type_mapping_handles_enum_and_unsigned()
    {
        var (edm, _, allowed) = MySqlIntrospector.MapType("enum", "enum('open','shipped','cancelled')");
        Assert.Equal("Edm.String", edm);
        Assert.Equal(new[] { "open", "shipped", "cancelled" }, allowed!);

        Assert.Equal("Edm.Int64", MySqlIntrospector.MapType("int", "int unsigned").EdmType);
        Assert.Equal("Edm.Int32", MySqlIntrospector.MapType("int", "int").EdmType);
        Assert.Equal("Edm.Boolean", MySqlIntrospector.MapType("tinyint", "tinyint(1)").EdmType);
    }

    [Fact]
    public void SqlServer_type_mapping()
    {
        Assert.Equal(("Edm.Guid", false), SqlServerIntrospector.MapType("uniqueidentifier"));
        Assert.Equal(("Edm.DateTimeOffset", false), SqlServerIntrospector.MapType("datetime2"));
        Assert.Equal(("Edm.Decimal", false), SqlServerIntrospector.MapType("money"));
        Assert.Equal(("Edm.String", true), SqlServerIntrospector.MapType("geography")); // fallback
    }

    // ---- safety invariant holds across all dialects ----

    [Theory]
    [InlineData("mysql")]
    [InlineData("sqlserver")]
    [InlineData("sqlite")]
    public void Injection_payloads_stay_parameterized_on_every_engine(string engine)
    {
        ISqlDialect dialect = engine switch
        {
            "mysql" => new MySqlDialect(),
            "sqlserver" => new SqlServerDialect(),
            _ => new SqliteDialect(),
        };

        const string payload = "'; DROP TABLE orders; --";
        var compiled = new SqlCompiler(dialect).CompileSelect(Schema, Query() with
        {
            Filter = new ComparisonNode(new FieldRef("status"), ComparisonOp.Eq, new ConstantValue(payload)),
        });

        Assert.DoesNotContain("DROP", compiled.Sql);
        Assert.Equal(payload, Assert.Single(compiled.Parameters).Value);
    }
}
