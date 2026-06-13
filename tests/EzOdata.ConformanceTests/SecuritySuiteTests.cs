using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace EzOdata.ConformanceTests;

/// <summary>
/// The release-gating security suite (spec 08 §11 / spec 13 §4): an SQL-injection corpus
/// driven through every surface, cursor tampering, and the masked/denied leak checks.
/// </summary>
[Collection("conformance")]
public class SecuritySuiteTests
{
    private readonly ConformanceFixture _fixture;

    public SecuritySuiteTests(ConformanceFixture fixture) => _fixture = fixture;

    public static readonly string[] SqliCorpus =
    [
        "x'; DROP TABLE customers; --",
        "1 OR 1=1",
        "' OR ''='",
        "'; EXEC xp_cmdshell('dir'); --",
        "1; DELETE FROM orders",
        "' UNION SELECT password FROM users --",
        "admin'--",
        "\"; DROP TABLE customers; --",
    ];

    [Fact]
    public async Task Sqli_corpus_through_odata_filter_never_executes()
    {
        foreach (var payload in SqliCorpus)
        {
            var url = $"/api/odata/{ConformanceFixture.ServiceName}/customers?$filter=name eq '{payload.Replace("'", "''")}'&$count=true";
            var response = await _fixture.Client.GetAsync(url);
            // Either a clean 200 with 0 matches (literal) or a 400 parse error — never a 500/data leak.
            Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.BadRequest,
                $"Payload '{payload}' produced {(int)response.StatusCode}.");

            if (response.StatusCode == HttpStatusCode.OK)
            {
                var body = await response.Content.ReadFromJsonAsync<JsonElement>();
                Assert.Equal(0, body.GetProperty("@odata.count").GetInt64());
            }
        }

        // Tables still intact
        var check = await _fixture.Client.GetFromJsonAsync<JsonElement>(
            $"/api/odata/{ConformanceFixture.ServiceName}/customers/$count");
        Assert.Equal(8, check.GetInt32());
    }

    [Fact]
    public async Task Sqli_corpus_through_rest_filter_never_executes()
    {
        foreach (var payload in SqliCorpus)
        {
            var url = $"/api/rest/{ConformanceFixture.ServiceName}/_table/customers?filter=name='{payload.Replace("'", "''")}'&include_count=true";
            var response = await _fixture.Client.GetAsync(url);
            Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.BadRequest,
                $"REST payload '{payload}' produced {(int)response.StatusCode}.");
        }
    }

    [Fact]
    public async Task Sqli_corpus_through_write_payload_is_stored_as_literal()
    {
        var email = $"sqli-{Guid.NewGuid():N}@x.example";
        var payload = "Robert'); DROP TABLE customers; --";

        var create = await _fixture.Client.PostAsJsonAsync(
            $"/api/odata/{ConformanceFixture.ServiceName}/customers", new { name = payload, email });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var id = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        // Stored verbatim; table intact
        var fetched = await _fixture.Client.GetFromJsonAsync<JsonElement>(
            $"/api/odata/{ConformanceFixture.ServiceName}/customers({id})");
        Assert.Equal(payload, fetched.GetProperty("name").GetString());

        var count = await _fixture.Client.GetFromJsonAsync<JsonElement>(
            $"/api/odata/{ConformanceFixture.ServiceName}/customers/$count");
        Assert.True(count.GetInt32() >= 8); // table still exists, row added

        await _fixture.Client.DeleteAsync($"/api/odata/{ConformanceFixture.ServiceName}/customers({id})");
    }

    [Fact]
    public async Task Tampered_skiptoken_is_rejected()
    {
        // Get a legit nextLink, then mutate the token.
        var page = await _fixture.Client.GetFromJsonAsync<JsonElement>(
            $"/api/odata/{ConformanceFixture.ServiceName}/customers?$top=3");
        var nextLink = page.GetProperty("@odata.nextLink").GetString()!;
        var token = System.Web.HttpUtility.ParseQueryString(new Uri(nextLink).Query)["$skiptoken"]!;

        // Flip a character of the signed token.
        var tampered = token[..^2] + (token[^1] == 'A' ? "BB" : "AA");
        var response = await _fixture.Client.GetAsync(
            $"/api/odata/{ConformanceFixture.ServiceName}/customers?$top=3&$skiptoken={Uri.EscapeDataString(tampered)}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Identifier_injection_via_select_is_rejected()
    {
        // Attempt to smuggle SQL via a field name (resolved against the schema, not interpolated).
        var url = $"/api/odata/{ConformanceFixture.ServiceName}/customers?$select=" +
                  Uri.EscapeDataString("id,(select password from users)");
        var response = await _fixture.Client.GetAsync(url);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
