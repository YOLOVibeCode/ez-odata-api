using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Xunit;

namespace EzOdata.IntegrationTests;

/// <summary>
/// SQLite engine end-to-end (spec 14 M4): introspection → OData reads/writes against
/// a real database file, no container required.
/// </summary>
public class SqliteEndToEndTests : IClassFixture<HostFixture>, IAsyncLifetime
{
    private readonly HostFixture _fixture;
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"ez-sqlite-e2e-{Guid.NewGuid():N}.db");
    private HttpClient _client = null!;

    public SqliteEndToEndTests(HostFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        // Build the fixture database file
        await using (var connection = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await connection.OpenAsync();
            var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE authors (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    name TEXT NOT NULL,
                    country VARCHAR(2)
                );
                CREATE TABLE books (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    author_id INTEGER NOT NULL REFERENCES authors(id),
                    title TEXT NOT NULL,
                    price DECIMAL(10,2) NOT NULL DEFAULT 0,
                    published DATETIME
                );
                INSERT INTO authors (name, country) VALUES
                    ('Ursula K. Le Guin', 'US'), ('Stanisław Lem', 'PL'), ('Iain Banks', 'GB');
                INSERT INTO books (author_id, title, price, published) VALUES
                    (1, 'The Dispossessed', 12.99, '1974-05-01T00:00:00Z'),
                    (1, 'The Left Hand of Darkness', 11.50, '1969-03-01T00:00:00Z'),
                    (2, 'Solaris', 9.99, '1961-06-01T00:00:00Z'),
                    (3, 'The Player of Games', 13.25, '1988-08-01T00:00:00Z');
                """;
            await command.ExecuteNonQueryAsync();
        }

        _client = await _fixture.CreateAdminClientAsync();

        var create = await _client.PostAsJsonAsync("/system/services", new
        {
            name = "library",
            label = "Library (SQLite)",
            connectorType = "sqlite",
            connection = new { filePath = _dbPath },
        });
        create.EnsureSuccessStatusCode();
        var serviceId = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();

        // Wait for introspection
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var service = await _client.GetFromJsonAsync<JsonElement>($"/system/services/{serviceId}");
            var status = service.GetProperty("status").GetString();
            if (status == "Active") return;
            if (status == "Failed")
            {
                throw new InvalidOperationException("SQLite introspection failed: " + service.GetProperty("statusDetail"));
            }

            await Task.Delay(300);
        }

        throw new TimeoutException("SQLite service did not become Active.");
    }

    public Task DisposeAsync()
    {
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Sqlite_full_lifecycle_reads_filters_and_writes()
    {
        // Read with filter + orderby
        var books = await _client.GetFromJsonAsync<JsonElement>(
            "/api/odata/library/books?$filter=price gt 10&$orderby=price desc&$select=title,price&$count=true");
        Assert.Equal(3, books.GetProperty("@odata.count").GetInt64());
        Assert.Equal("The Player of Games", books.GetProperty("value")[0].GetProperty("title").GetString());

        // Navigation filter (FK discovered by pragma introspection)
        var usBooks = await _client.GetFromJsonAsync<JsonElement>(
            "/api/odata/library/books/$count?$filter=author/country eq 'US'");
        Assert.Equal(2, usBooks.GetInt32());

        // Lambda
        var prolific = await _client.GetFromJsonAsync<JsonElement>(
            "/api/odata/library/authors?$filter=books/any(b: b/price lt 10)&$select=name");
        Assert.Equal("Stanisław Lem", prolific.GetProperty("value")[0].GetProperty("name").GetString());

        // Insert with RETURNING (rowid auto key)
        var create = await _client.PostAsJsonAsync("/api/odata/library/authors",
            new { name = "Octavia Butler", country = "US" });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var author = await create.Content.ReadFromJsonAsync<JsonElement>();
        var id = author.GetProperty("id").GetInt64();
        Assert.True(id > 3);

        // Update + delete
        var patch = await _client.PatchAsJsonAsync($"/api/odata/library/authors({id})", new { country = "CA" });
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);

        var delete = await _client.DeleteAsync($"/api/odata/library/authors({id})");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        // FK violation taxonomy
        var badFk = await _client.PostAsJsonAsync("/api/odata/library/books",
            new { author_id = 9999, title = "Ghost", price = 1.0 });
        Assert.Equal(HttpStatusCode.Conflict, badFk.StatusCode);
        Assert.Contains("Conflict.ForeignKeyViolation", await badFk.Content.ReadAsStringAsync());

        // Year function via strftime
        var vintage = await _client.GetFromJsonAsync<JsonElement>(
            "/api/odata/library/books/$count?$filter=year(published) lt 1980");
        Assert.Equal(3, vintage.GetInt32());
    }
}
