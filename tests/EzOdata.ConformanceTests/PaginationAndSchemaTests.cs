using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace EzOdata.ConformanceTests;

[Collection("conformance")]
public class PaginationAndSchemaTests
{
    private readonly ConformanceFixture _fixture;

    public PaginationAndSchemaTests(ConformanceFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Following_nextLinks_visits_every_row_exactly_once()
    {
        var seen = new List<int>();
        var url = $"/api/odata/{ConformanceFixture.ServiceName}/customers?$top=3&$select=id&$orderby=id";

        for (var hops = 0; hops < 10 && url is not null; hops++)
        {
            var page = await _fixture.Client.GetFromJsonAsync<JsonElement>(url);
            seen.AddRange(page.GetProperty("value").EnumerateArray().Select(v => v.GetProperty("id").GetInt32()));

            url = page.TryGetProperty("@odata.nextLink", out var next)
                ? new Uri(next.GetString()!).PathAndQuery
                : null;
        }

        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8], seen);
    }

    [Fact]
    public async Task Introspected_schema_has_expected_shape()
    {
        var schema = await _fixture.Client.GetFromJsonAsync<JsonElement>("/system/services/1/schema");
        var tables = schema.GetProperty("tables").EnumerateArray().ToList();

        // Northwind fixture: 7 tables + 1 view (spec 13 §2)
        Assert.Contains(tables, t => t.GetProperty("exposedName").GetString() == "customers");
        Assert.Contains(tables, t => t.GetProperty("exposedName").GetString() == "v_customer_order_totals"
                                      && t.GetProperty("isView").GetBoolean()
                                      && !t.GetProperty("writable").GetBoolean());

        // Composite PK
        var orderItems = tables.Single(t => t.GetProperty("exposedName").GetString() == "order_items");
        Assert.Equal(new[] { "order_id", "line_no" },
            orderItems.GetProperty("primaryKey").EnumerateArray().Select(k => k.GetString()!).ToArray());

        // FK navigation naming (spec 04 §5.3): customer_id → customer / orders
        var orders = tables.Single(t => t.GetProperty("exposedName").GetString() == "orders");
        var fk = orders.GetProperty("foreignKeys").EnumerateArray()
            .Single(f => f.GetProperty("refTable").GetString() == "customers");
        Assert.Equal("customer", fk.GetProperty("navToOne").GetString());
        Assert.Equal("orders", fk.GetProperty("navToMany").GetString());

        // Self-referencing FK on employees
        var employees = tables.Single(t => t.GetProperty("exposedName").GetString() == "employees");
        var selfFk = employees.GetProperty("foreignKeys").EnumerateArray().Single();
        Assert.Equal("manager", selfFk.GetProperty("navToOne").GetString());

        // Keyless table exposed without a primary key
        var noPk = tables.Single(t => t.GetProperty("exposedName").GetString() == "no_pk_log");
        Assert.Empty(noPk.GetProperty("primaryKey").EnumerateArray());

        // Type zoo mappings (spec 04 §6)
        var zoo = tables.Single(t => t.GetProperty("exposedName").GetString() == "type_zoo");
        var columns = zoo.GetProperty("columns").EnumerateArray()
            .ToDictionary(c => c.GetProperty("exposedName").GetString()!, c => c.GetProperty("edmType").GetString());
        Assert.Equal("Edm.Int16", columns["a_int2"]);
        Assert.Equal("Edm.Int64", columns["a_int8"]);
        Assert.Equal("Edm.Decimal", columns["a_num"]);
        Assert.Equal("Edm.Single", columns["a_float4"]);
        Assert.Equal("Edm.Double", columns["a_float8"]);
        Assert.Equal("Edm.Boolean", columns["a_bool"]);
        Assert.Equal("Edm.Guid", columns["a_uuid"]);
        Assert.Equal("Edm.Date", columns["a_date"]);
        Assert.Equal("Edm.TimeOfDay", columns["a_time"]);
        Assert.Equal("Edm.DateTimeOffset", columns["a_tstz"]);
        Assert.Equal("Edm.Binary", columns["a_bytes"]);
        Assert.Equal("Edm.Untyped", columns["a_json"]);
        Assert.Equal("Collection(Edm.String)", columns["a_textarr"]);

        // Comments propagate (spec 04 CON-2)
        var customers = tables.Single(t => t.GetProperty("exposedName").GetString() == "customers");
        Assert.Equal("CRM master table", customers.GetProperty("comment").GetString());
    }

    [Fact]
    public async Task Type_zoo_row_roundtrips_through_odata()
    {
        var response = await _fixture.Client.GetFromJsonAsync<JsonElement>(
            $"/api/odata/{ConformanceFixture.ServiceName}/type_zoo(1)");

        Assert.Equal(7, response.GetProperty("a_int2").GetInt32());
        Assert.Equal(9007199254740993L, response.GetProperty("a_int8").GetInt64());
        Assert.True(response.GetProperty("a_bool").GetBoolean());
        Assert.Equal("a1b2c3d4-e5f6-4711-8899-aabbccddeeff", response.GetProperty("a_uuid").GetString());
        Assert.Equal("2026-06-12", response.GetProperty("a_date").GetString());
    }
}
