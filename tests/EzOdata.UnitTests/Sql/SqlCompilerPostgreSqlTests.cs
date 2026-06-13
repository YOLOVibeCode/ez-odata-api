using EzOdata.Connectors.Abstractions;
using EzOdata.Connectors.Abstractions.Sql;
using EzOdata.Connectors.PostgreSql;
using EzOdata.Core.Query;
using EzOdata.Core.Schema;
using Xunit;

namespace EzOdata.UnitTests.Sql;

/// <summary>
/// Dialect translation tests for PostgreSQL (spec 04 §7.2) plus the SQL safety
/// invariants of spec 13 §4: client-supplied values must never appear in SQL text.
/// </summary>
public class SqlCompilerPostgreSqlTests
{
    private const string Taint = "EZTAINT_7f3a"; // any appearance in SQL text = injection bug

    private static readonly SchemaSnapshot Schema = new()
    {
        Engine = "postgresql",
        Tables =
        [
            new TableModel
            {
                DbSchema = "public", DbName = "customers", ExposedName = "customers",
                PrimaryKey = ["id"],
                Columns =
                [
                    new ColumnModel { DbName = "id", ExposedName = "id", DbType = "integer", EdmType = "Edm.Int32", IsPrimaryKey = true },
                    new ColumnModel { DbName = "name", ExposedName = "name", DbType = "text", EdmType = "Edm.String" },
                    new ColumnModel { DbName = "country", ExposedName = "country", DbType = "text", EdmType = "Edm.String" },
                    new ColumnModel { DbName = "created_at", ExposedName = "created_at", DbType = "timestamptz", EdmType = "Edm.DateTimeOffset" },
                ],
            },
            new TableModel
            {
                DbSchema = "public", DbName = "orders", ExposedName = "orders",
                PrimaryKey = ["id"],
                Columns =
                [
                    new ColumnModel { DbName = "id", ExposedName = "id", DbType = "bigint", EdmType = "Edm.Int64", IsPrimaryKey = true },
                    new ColumnModel { DbName = "customer_id", ExposedName = "customer_id", DbType = "integer", EdmType = "Edm.Int32" },
                    new ColumnModel { DbName = "total", ExposedName = "total", DbType = "numeric", EdmType = "Edm.Decimal" },
                    new ColumnModel { DbName = "status", ExposedName = "status", DbType = "text", EdmType = "Edm.String" },
                ],
                ForeignKeys =
                [
                    new ForeignKeyModel
                    {
                        Name = "fk_orders_customer", Columns = ["customer_id"],
                        RefTable = "customers", RefColumns = ["id"],
                        NavToOne = "customer", NavToMany = "orders",
                    },
                ],
            },
        ],
    };

    private static readonly SqlCompiler Compiler = new(new PostgreSqlDialect());

    private static QueryRequest Query(string table = "customers") => new() { ServiceName = "svc", Table = table };

    [Fact]
    public void Plain_select_projects_all_columns_with_pk_tiebreaker()
    {
        var compiled = Compiler.CompileSelect(Schema, Query() with { Top = 25 });

        Assert.Equal(
            "SELECT t0.\"id\" AS \"id\", t0.\"name\" AS \"name\", t0.\"country\" AS \"country\", t0.\"created_at\" AS \"created_at\"" +
            " FROM \"public\".\"customers\" AS t0 ORDER BY t0.\"id\" LIMIT 26",
            compiled.Sql);
        Assert.Empty(compiled.Parameters);
    }

    [Fact]
    public void Select_projection_limits_columns()
    {
        var compiled = Compiler.CompileSelect(Schema, Query() with { Select = ["id", "name"] });
        Assert.StartsWith("SELECT t0.\"id\" AS \"id\", t0.\"name\" AS \"name\" FROM", compiled.Sql);
    }

    [Fact]
    public void Unknown_select_field_throws_unknown_property()
    {
        var ex = Assert.Throws<QueryValidationException>(() =>
            Compiler.CompileSelect(Schema, Query() with { Select = ["nope"] }));
        Assert.Equal("Validation.UnknownProperty", ex.ErrorCode);
    }

    [Fact]
    public void Comparison_values_are_parameterized()
    {
        var query = Query() with
        {
            Filter = new ComparisonNode(new FieldRef("name"), ComparisonOp.Eq, new ConstantValue(Taint)),
        };
        var compiled = Compiler.CompileSelect(Schema, query);

        Assert.Contains("WHERE t0.\"name\" = @p0", compiled.Sql);
        Assert.DoesNotContain(Taint, compiled.Sql);
        Assert.Equal(Taint, Assert.Single(compiled.Parameters).Value);
    }

    [Fact]
    public void Null_comparisons_render_is_null()
    {
        var query = Query() with
        {
            Filter = new ComparisonNode(new FieldRef("country"), ComparisonOp.Eq, ConstantValue.Null),
        };
        Assert.Contains("t0.\"country\" IS NULL", Compiler.CompileSelect(Schema, query).Sql);

        var ne = Query() with
        {
            Filter = new ComparisonNode(new FieldRef("country"), ComparisonOp.Ne, ConstantValue.Null),
        };
        Assert.Contains("t0.\"country\" IS NOT NULL", Compiler.CompileSelect(Schema, ne).Sql);
    }

    [Fact]
    public void Logical_composition_renders_parenthesized()
    {
        var query = Query("orders") with
        {
            Filter = new LogicalNode(LogicalOp.And,
            [
                new ComparisonNode(new FieldRef("status"), ComparisonOp.Eq, new ConstantValue("open")),
                new ComparisonNode(new FieldRef("total"), ComparisonOp.Gt, new ConstantValue(250.0m)),
            ]),
        };
        var compiled = Compiler.CompileSelect(Schema, query);

        Assert.Contains("WHERE (t0.\"status\" = @p0 AND t0.\"total\" > @p1)", compiled.Sql);
        Assert.Equal(2, compiled.Parameters.Count);
    }

    [Fact]
    public void Contains_compiles_to_ilike_with_escaped_pattern()
    {
        var query = Query() with
        {
            Filter = new FunctionNode(FilterFunction.Contains,
                [new FieldArg(new FieldRef("name")), new ConstantArg(new ConstantValue("50%_off"))]),
        };
        var compiled = Compiler.CompileSelect(Schema, query);

        Assert.Contains("t0.\"name\" ILIKE @p0 ESCAPE '\\'", compiled.Sql);
        Assert.Equal("%50\\%\\_off%", Assert.Single(compiled.Parameters).Value);
    }

    [Fact]
    public void Startswith_and_endswith_anchor_the_pattern()
    {
        var starts = Query() with
        {
            Filter = new FunctionNode(FilterFunction.StartsWith,
                [new FieldArg(new FieldRef("name")), new ConstantArg(new ConstantValue("Ac"))]),
        };
        Assert.Equal("Ac%", Assert.Single(Compiler.CompileSelect(Schema, starts).Parameters).Value);

        var ends = Query() with
        {
            Filter = new FunctionNode(FilterFunction.EndsWith,
                [new FieldArg(new FieldRef("name")), new ConstantArg(new ConstantValue("Corp"))]),
        };
        Assert.Equal("%Corp", Assert.Single(Compiler.CompileSelect(Schema, ends).Parameters).Value);
    }

    [Fact]
    public void Function_comparison_renders_extract_for_year()
    {
        var query = Query() with
        {
            Filter = new FunctionNode(FilterFunction.Year,
                [new FieldArg(new FieldRef("created_at"))], ComparisonOp.Eq, new ConstantValue(2026)),
        };
        var compiled = Compiler.CompileSelect(Schema, query);

        Assert.Contains("EXTRACT(YEAR FROM t0.\"created_at\")::int = @p0", compiled.Sql);
        Assert.Equal(2026, Assert.Single(compiled.Parameters).Value);
    }

    [Fact]
    public void In_list_parameterizes_every_value()
    {
        var query = Query("orders") with
        {
            Filter = new InNode(new FieldRef("status"),
                [new ConstantValue("open"), new ConstantValue("shipped"), new ConstantValue(Taint)]),
        };
        var compiled = Compiler.CompileSelect(Schema, query);

        Assert.Contains("t0.\"status\" IN (@p0, @p1, @p2)", compiled.Sql);
        Assert.DoesNotContain(Taint, compiled.Sql);
        Assert.Equal(3, compiled.Parameters.Count);
    }

    [Fact]
    public void To_one_navigation_in_filter_compiles_to_left_join()
    {
        var query = Query("orders") with
        {
            Filter = new ComparisonNode(new FieldRef(["customer", "country"]), ComparisonOp.Eq, new ConstantValue("DE")),
        };
        var compiled = Compiler.CompileSelect(Schema, query);

        Assert.Contains("LEFT JOIN \"public\".\"customers\" AS t1 ON t0.\"customer_id\" = t1.\"id\"", compiled.Sql);
        Assert.Contains("WHERE t1.\"country\" = @p0", compiled.Sql);
    }

    [Fact]
    public void Any_lambda_compiles_to_exists()
    {
        var query = Query() with
        {
            Filter = new LambdaNode("orders", LambdaKind.Any,
                new ComparisonNode(new FieldRef("total"), ComparisonOp.Gt, new ConstantValue(100m))),
        };
        var compiled = Compiler.CompileSelect(Schema, query);

        Assert.Contains(
            "EXISTS (SELECT 1 FROM \"public\".\"orders\" AS l0 WHERE l0.\"customer_id\" = t0.\"id\" AND l0.\"total\" > @p0)",
            compiled.Sql);
    }

    [Fact]
    public void All_lambda_compiles_to_double_negation()
    {
        var query = Query() with
        {
            Filter = new LambdaNode("orders", LambdaKind.All,
                new ComparisonNode(new FieldRef("status"), ComparisonOp.Eq, new ConstantValue("shipped"))),
        };
        Assert.Contains("NOT EXISTS (SELECT 1 FROM", Compiler.CompileSelect(Schema, query).Sql);
        Assert.Contains("AND NOT (l0.\"status\" = @p0)", Compiler.CompileSelect(Schema, query).Sql);
    }

    [Fact]
    public void Orderby_appends_pk_tiebreaker_once()
    {
        var compiled = Compiler.CompileSelect(Schema, Query() with
        {
            OrderBy = [new OrderByItem("name", Descending: true)],
        });
        Assert.Contains("ORDER BY t0.\"name\" DESC, t0.\"id\"", compiled.Sql);

        var byPk = Compiler.CompileSelect(Schema, Query() with
        {
            OrderBy = [new OrderByItem("id", Descending: false)],
        });
        Assert.Contains("ORDER BY t0.\"id\"", byPk.Sql);
        Assert.DoesNotContain("t0.\"id\", t0.\"id\"", byPk.Sql);
    }

    [Fact]
    public void Skip_renders_offset()
    {
        var compiled = Compiler.CompileSelect(Schema, Query() with { Top = 10, Skip = 30 });
        Assert.EndsWith("LIMIT 11 OFFSET 30", compiled.Sql);
    }

    [Fact]
    public void Count_uses_same_where_without_pagination()
    {
        var query = Query("orders") with
        {
            Filter = new ComparisonNode(new FieldRef("status"), ComparisonOp.Eq, new ConstantValue("open")),
            Top = 5,
            Skip = 10,
        };
        var compiled = Compiler.CompileCount(Schema, query);

        Assert.Equal("SELECT COUNT(*) FROM \"public\".\"orders\" AS t0 WHERE t0.\"status\" = @p0", compiled.Sql);
    }

    [Fact]
    public void Unknown_table_throws()
    {
        Assert.Throws<QueryValidationException>(() => Compiler.CompileSelect(Schema, Query("ghosts")));
    }

    [Fact]
    public void Sql_injection_attempts_stay_in_parameters()
    {
        var payloads = new[]
        {
            "'; DROP TABLE customers; --",
            "1 OR 1=1",
            "\" OR \"\"=\"",
            "'; EXEC xp_cmdshell('dir'); --",
        };

        foreach (var payload in payloads)
        {
            var query = Query() with
            {
                Filter = new ComparisonNode(new FieldRef("name"), ComparisonOp.Eq, new ConstantValue(payload)),
            };
            var compiled = Compiler.CompileSelect(Schema, query);

            Assert.DoesNotContain("DROP", compiled.Sql);
            Assert.DoesNotContain("xp_cmdshell", compiled.Sql);
            Assert.DoesNotContain(payload, compiled.Sql);
            Assert.Equal(payload, Assert.Single(compiled.Parameters).Value);
        }
    }
}
