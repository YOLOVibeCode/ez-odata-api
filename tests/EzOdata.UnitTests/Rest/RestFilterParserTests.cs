using EzOdata.Connectors.Abstractions;
using EzOdata.Connectors.Abstractions.Sql;
using EzOdata.Connectors.PostgreSql;
using EzOdata.Core.Query;
using EzOdata.Core.Schema;
using EzOdata.Rest;
using Xunit;

namespace EzOdata.UnitTests.Rest;

/// <summary>REST SQL-ish filter grammar (spec 06 §5) → IR, then verified through the compiler.</summary>
public class RestFilterParserTests
{
    [Fact]
    public void Equality_string()
    {
        var node = Assert.IsType<ComparisonNode>(RestFilterParser.Parse("status = 'open'"));
        Assert.Equal("status", node.Field.Leaf);
        Assert.Equal(ComparisonOp.Eq, node.Op);
        Assert.Equal("open", node.Value.Value);
    }

    [Fact]
    public void Numeric_comparisons()
    {
        var gt = Assert.IsType<ComparisonNode>(RestFilterParser.Parse("total > 250.5"));
        Assert.Equal(ComparisonOp.Gt, gt.Op);
        Assert.Equal(250.5m, gt.Value.Value);

        var ne = Assert.IsType<ComparisonNode>(RestFilterParser.Parse("qty != 0"));
        Assert.Equal(ComparisonOp.Ne, ne.Op);
        Assert.Equal(0L, Assert.IsType<long>(ne.Value.Value));
    }

    [Fact]
    public void And_or_precedence_and_grouping()
    {
        // a=1 or b=2 and c=3  →  a=1 OR (b=2 AND c=3)
        var top = Assert.IsType<LogicalNode>(RestFilterParser.Parse("a = 1 or b = 2 and c = 3"));
        Assert.Equal(LogicalOp.Or, top.Op);
        Assert.IsType<LogicalNode>(top.Operands[1]);

        var grouped = Assert.IsType<LogicalNode>(RestFilterParser.Parse("(a = 1 or b = 2) and c = 3"));
        Assert.Equal(LogicalOp.And, grouped.Op);
    }

    [Fact]
    public void Not_negation()
    {
        var not = Assert.IsType<NotNode>(RestFilterParser.Parse("not (status = 'closed')"));
        Assert.IsType<ComparisonNode>(not.Operand);
    }

    [Fact]
    public void Is_null_and_is_not_null()
    {
        var isNull = Assert.IsType<ComparisonNode>(RestFilterParser.Parse("ssn is null"));
        Assert.Equal(ComparisonOp.Eq, isNull.Op);
        Assert.Null(isNull.Value.Value);

        var notNull = Assert.IsType<ComparisonNode>(RestFilterParser.Parse("ssn is not null"));
        Assert.Equal(ComparisonOp.Ne, notNull.Op);
    }

    [Fact]
    public void In_list()
    {
        var inNode = Assert.IsType<InNode>(RestFilterParser.Parse("country in ('US','DE','JP')"));
        Assert.Equal(3, inNode.Values.Count);
        Assert.Equal("US", inNode.Values[0].Value);
    }

    [Fact]
    public void Contains_starts_ends()
    {
        Assert.Equal(FilterFunction.Contains, ((FunctionNode)RestFilterParser.Parse("name contains 'acme'")).Function);
        Assert.Equal(FilterFunction.StartsWith, ((FunctionNode)RestFilterParser.Parse("name starts with 'Ac'")).Function);
        Assert.Equal(FilterFunction.EndsWith, ((FunctionNode)RestFilterParser.Parse("name ends with 'Corp'")).Function);
    }

    [Fact]
    public void Like_maps_to_contains_stripping_wildcards()
    {
        var fn = Assert.IsType<FunctionNode>(RestFilterParser.Parse("name like '%acme%'"));
        Assert.Equal(FilterFunction.Contains, fn.Function);
        Assert.Equal("acme", ((ConstantArg)fn.Args[1]).Value.Value);
    }

    [Fact]
    public void Navigation_path_depth_one()
    {
        var node = Assert.IsType<ComparisonNode>(RestFilterParser.Parse("customer.country = 'US'"));
        Assert.True(node.Field.IsNavigated);
        Assert.Equal(["customer", "country"], node.Field.Path);
    }

    [Theory]
    [InlineData("status =")]
    [InlineData("= 'x'")]
    [InlineData("status ?? 'x'")]
    [InlineData("(status = 'x'")]
    public void Malformed_filters_throw(string input)
    {
        Assert.Throws<QueryValidationException>(() => RestFilterParser.Parse(input));
    }

    [Fact]
    public void Injection_payload_becomes_a_literal_through_the_compiler()
    {
        var schema = new SchemaSnapshot
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
                        new ColumnModel { DbName = "id", ExposedName = "id", DbType = "int", EdmType = "Edm.Int32", IsPrimaryKey = true },
                        new ColumnModel { DbName = "name", ExposedName = "name", DbType = "text", EdmType = "Edm.String" },
                    ],
                },
            ],
        };

        var filter = RestFilterParser.Parse("name = 'x'' OR 1=1 --'");
        var compiled = new SqlCompiler(new PostgreSqlDialect())
            .CompileSelect(schema, new QueryRequest { ServiceName = "s", Table = "customers", Filter = filter });

        Assert.DoesNotContain("OR 1=1", compiled.Sql);
        Assert.Equal("x' OR 1=1 --", Assert.Single(compiled.Parameters).Value);
    }
}
