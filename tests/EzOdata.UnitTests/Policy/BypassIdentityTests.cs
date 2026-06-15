using EzOdata.Core.Policy;
using EzOdata.Core.Query;
using EzOdata.Core.Schema;
using Xunit;

namespace EzOdata.UnitTests.Policy;

/// <summary>
/// RequestIdentity.Bypass short-circuits PolicyEngine and SnapshotTrimmer (plan §core-bypass).
/// </summary>
public class BypassIdentityTests
{
    private static readonly PolicyEngine Engine = new();

    private static RowFilterParser FailParser =>
        (_, _) => throw new InvalidOperationException("RowFilterParser must not be called for a bypass identity.");

    // ---- PolicyEngine ----

    [Fact]
    public void Bypass_identity_gets_full_access_regardless_of_rules()
    {
        var decision = Engine.Authorize(
            RequestIdentity.DevBypass,
            roleRules: [],               // no roles
            serviceName: "sales",
            table: "orders",
            verb: Verb.Delete,           // normally the most restricted verb
            tableColumns: ["id", "total", "ssn"],
            rowFilterParser: FailParser);

        Assert.True(decision.Allowed);
        Assert.True(decision.Bypass);
        Assert.Empty(decision.DeniedFields);
    }

    [Fact]
    public void Bypass_identity_row_filter_parser_is_never_called()
    {
        // Verifies the parser delegate is not invoked (it throws if it is).
        var decision = Engine.Authorize(
            RequestIdentity.DevBypass, [], "s", "t", Verb.Get, ["id"], FailParser);

        Assert.True(decision.Allowed);
    }

    [Fact]
    public void Anonymous_identity_is_not_a_bypass()
    {
        Assert.False(RequestIdentity.Anonymous.Bypass);
        Assert.False(RequestIdentity.DevBypass == RequestIdentity.Anonymous);
        Assert.True(RequestIdentity.DevBypass.Bypass);
    }

    // ---- SnapshotTrimmer ----

    [Fact]
    public void Bypass_identity_returns_untrimmed_snapshot()
    {
        var snapshot = new SchemaSnapshot
        {
            Engine = "postgresql",
            Tables =
            [
                new TableModel
                {
                    DbSchema = "public", DbName = "secrets", ExposedName = "secrets",
                    PrimaryKey = ["id"],
                    Columns =
                    [
                        new ColumnModel { DbName = "id", ExposedName = "id", DbType = "int", EdmType = "Edm.Int32", IsPrimaryKey = true },
                        new ColumnModel { DbName = "ssn", ExposedName = "ssn", DbType = "text", EdmType = "Edm.String" },
                    ],
                },
                new TableModel
                {
                    DbSchema = "public", DbName = "hidden", ExposedName = "hidden",
                    PrimaryKey = ["id"],
                    Columns = [new ColumnModel { DbName = "id", ExposedName = "id", DbType = "int", EdmType = "Edm.Int32", IsPrimaryKey = true }],
                },
            ],
        };

        // Bypass identity with zero roles — normally everything would be invisible.
        var trimmed = SnapshotTrimmer.Trim(snapshot, RequestIdentity.DevBypass, [], "sales", Engine, FailParser);

        Assert.Equal(2, trimmed.Tables.Count);
        var secretsTable = trimmed.Tables.First(t => t.ExposedName == "secrets");
        Assert.Equal(2, secretsTable.Columns.Count); // ssn not stripped
    }
}
