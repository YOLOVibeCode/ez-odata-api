using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Testcontainers.PostgreSql;
using Xunit;

namespace EzOdata.ConformanceTests;

/// <summary>
/// One PostgreSQL container + one host + one 'sales' service for the whole
/// conformance collection. Setup: start PG, apply the northwind fixture, boot the
/// host, complete setup, create the service, wait for introspection to go Active.
/// </summary>
public sealed class ConformanceFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"ez-conf-{Guid.NewGuid():N}.db");

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .WithDatabase("northwind")
        .WithUsername("ez")
        .WithPassword("ez-test-password")
        .Build();

    public HttpClient Client { get; private set; } = null!;

    public const string ServiceName = "sales";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("SystemDatabase:Provider", "sqlite");
        builder.UseSetting("SystemDatabase:ConnectionString", $"Data Source={_dbPath}");
        builder.UseSetting("Auth:Jwt:SigningKey", "conformance-test-signing-key-32-ch!");
        builder.UseSetting("Encryption:MasterKey", Convert.ToBase64String("conformance-test-master-key-32b!"u8.ToArray()));
    }

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        var sql = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "northwind.sql"));
        var execResult = await _postgres.ExecScriptAsync(sql);
        if (execResult.ExitCode != 0)
        {
            throw new InvalidOperationException($"Fixture SQL failed: {execResult.Stderr}");
        }

        Client = CreateClient();

        // First-run setup + login
        var setup = await Client.PostAsJsonAsync("/system/setup",
            new { email = "admin@example.com", displayName = "Admin", password = "conformance-password-1" });
        setup.EnsureSuccessStatusCode();

        var login = await Client.PostAsJsonAsync("/system/auth/login",
            new { email = "admin@example.com", password = "conformance-password-1" });
        var auth = await login.Content.ReadFromJsonAsync<JsonElement>();
        Client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", auth.GetProperty("accessToken").GetString());

        // Create the service against the container
        var create = await Client.PostAsJsonAsync("/system/services", new
        {
            name = ServiceName,
            label = "Sales (conformance)",
            connectorType = "postgresql",
            connection = new
            {
                host = _postgres.Hostname,
                port = _postgres.GetMappedPublicPort(5432),
                database = "northwind",
                username = "ez",
                password = "ez-test-password",
                tls = new { mode = "disable" },
            },
        });
        create.EnsureSuccessStatusCode();
        var serviceId = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();

        await WaitForActiveAsync(serviceId, TimeSpan.FromSeconds(60));
    }

    private async Task WaitForActiveAsync(long serviceId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var service = await Client.GetFromJsonAsync<JsonElement>($"/system/services/{serviceId}");
            var status = service.GetProperty("status").GetString();
            switch (status)
            {
                case "Active":
                    return;
                case "Failed":
                    throw new InvalidOperationException(
                        $"Introspection failed: {service.GetProperty("statusDetail")}");
            }

            await Task.Delay(500);
        }

        throw new TimeoutException("Service did not become Active within the timeout.");
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await _postgres.DisposeAsync();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
    }
}

[CollectionDefinition("conformance")]
public sealed class ConformanceCollection : ICollectionFixture<ConformanceFixture>;
