using EzOdata.Core.Policy;
using EzOdata.Core.Query;
using EzOdata.Core.Schema;
using Xunit;

namespace EzOdata.UnitTests.Policy;

/// <summary>
/// The leak matrix (spec 08 §11 item 2): a restricted field must be unobservable
/// through every read path — implicit select, explicit select, filter, orderby.
/// </summary>
public class QueryPolicyRewriterTests
{
    private static readonly TableModel Customers = new()
    {
        DbSchema = "public", DbName = "customers", ExposedName = "customers",
        PrimaryKey = ["id"],
        Columns =
        [
            new ColumnModel { DbName = "id", ExposedName = "id", DbType = "int4", EdmType = "Edm.Int32", IsPrimaryKey = true },
            new ColumnModel { DbName = "name", ExposedName = "name", DbType = "text", EdmType = "Edm.String" },
            new ColumnModel { DbName = "email", ExposedName = "email", DbType = "text", EdmType = "Edm.String" },
            new ColumnModel { DbName = "ssn", ExposedName = "ssn", DbType = "text", EdmType = "Edm.String" },
        ],
    };

    private static QueryRequest Query() => new() { ServiceName = "sales", Table = "customers" };

    private static PolicyDecision DenySsn() => new()
    {
        Allowed = true,
        DeniedFields = new HashSet<string> { "ssn" },
    };

    [Fact]
    public void Implicit_select_excludes_denied_fields()
    {
        var rewritten = QueryPolicyRewriter.Rewrite(Query(), DenySsn(), Customers);

        Assert.NotNull(rewritten.Select);
        Assert.DoesNotContain("ssn", rewritten.Select!);
        Assert.Contains("name", rewritten.Select!);
    }

    [Fact]
    public void Explicit_select_of_denied_field_throws()
    {
        var query = Query() with { Select = ["id", "ssn"] };
        var ex = Assert.Throws<QueryPolicyRewriter.FieldDeniedException>(
            () => QueryPolicyRewriter.Rewrite(query, DenySsn(), Customers));
        Assert.Equal("ssn", ex.Field);
    }

    [Fact]
    public void Filter_on_denied_field_throws()
    {
        var query = Query() with
        {
            Filter = new ComparisonNode(new FieldRef("ssn"), ComparisonOp.Eq, new ConstantValue("111-22-3333")),
        };
        Assert.Throws<QueryPolicyRewriter.FieldDeniedException>(
            () => QueryPolicyRewriter.Rewrite(query, DenySsn(), Customers));
    }

    [Fact]
    public void Filter_function_arg_on_denied_field_throws()
    {
        var query = Query() with
        {
            Filter = new FunctionNode(FilterFunction.Contains,
                [new FieldArg(new FieldRef("ssn")), new ConstantArg(new ConstantValue("111"))]),
        };
        Assert.Throws<QueryPolicyRewriter.FieldDeniedException>(
            () => QueryPolicyRewriter.Rewrite(query, DenySsn(), Customers));
    }

    [Fact]
    public void Orderby_on_denied_field_throws()
    {
        var query = Query() with { OrderBy = [new OrderByItem("ssn", false)] };
        Assert.Throws<QueryPolicyRewriter.FieldDeniedException>(
            () => QueryPolicyRewriter.Rewrite(query, DenySsn(), Customers));
    }

    [Fact]
    public void Nested_filter_still_caught()
    {
        var query = Query() with
        {
            Filter = new LogicalNode(LogicalOp.Or,
            [
                new ComparisonNode(new FieldRef("name"), ComparisonOp.Eq, new ConstantValue("x")),
                new NotNode(new ComparisonNode(new FieldRef("ssn"), ComparisonOp.Ne, ConstantValue.Null)),
            ]),
        };
        Assert.Throws<QueryPolicyRewriter.FieldDeniedException>(
            () => QueryPolicyRewriter.Rewrite(query, DenySsn(), Customers));
    }

    [Fact]
    public void Masked_fields_removed_from_sql_but_reported_for_serialization()
    {
        var decision = new PolicyDecision
        {
            Allowed = true,
            MaskedFields = new Dictionary<string, string> { ["email"] = "***@***" },
        };

        var query = Query();
        var rewritten = QueryPolicyRewriter.Rewrite(query, decision, Customers);

        Assert.DoesNotContain("email", rewritten.Select!);
        Assert.Equal("***@***", QueryPolicyRewriter.MasksToApply(query, decision)["email"]);
    }

    [Fact]
    public void Masked_field_is_not_filterable()
    {
        var decision = new PolicyDecision
        {
            Allowed = true,
            MaskedFields = new Dictionary<string, string> { ["email"] = "***" },
        };
        var query = Query() with
        {
            Filter = new ComparisonNode(new FieldRef("email"), ComparisonOp.Eq, new ConstantValue("x@y.z")),
        };

        Assert.Throws<QueryPolicyRewriter.FieldDeniedException>(
            () => QueryPolicyRewriter.Rewrite(query, decision, Customers));
    }

    [Fact]
    public void Masked_field_in_explicit_select_is_allowed_but_stripped_from_sql()
    {
        var decision = new PolicyDecision
        {
            Allowed = true,
            MaskedFields = new Dictionary<string, string> { ["email"] = "***" },
        };
        var query = Query() with { Select = ["id", "email"] };

        var rewritten = QueryPolicyRewriter.Rewrite(query, decision, Customers);
        Assert.Equal(["id"], rewritten.Select!);
        Assert.Equal("***", QueryPolicyRewriter.MasksToApply(query, decision)["email"]);
    }

    [Fact]
    public void Row_filter_is_ANDed_with_client_filter()
    {
        var rowFilter = new ComparisonNode(new FieldRef("country"), ComparisonOp.Eq, new ConstantValue("US"));
        var clientFilter = new ComparisonNode(new FieldRef("name"), ComparisonOp.Eq, new ConstantValue("Acme"));
        var decision = new PolicyDecision { Allowed = true, RowFilter = rowFilter };

        var rewritten = QueryPolicyRewriter.Rewrite(Query() with { Filter = clientFilter }, decision, Customers);

        var and = Assert.IsType<LogicalNode>(rewritten.Filter);
        Assert.Equal(LogicalOp.And, and.Op);
        Assert.Contains(clientFilter, and.Operands);
        Assert.Contains(rowFilter, and.Operands);
    }

    [Fact]
    public void Bypass_decision_leaves_query_untouched()
    {
        var query = Query() with { Select = ["ssn"] };
        var rewritten = QueryPolicyRewriter.Rewrite(query, PolicyDecision.FullAccess, Customers);
        Assert.Same(query, rewritten);
    }
}
