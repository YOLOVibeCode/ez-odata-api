using System.Globalization;
using System.Text.Json;
using Xunit;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace EzOdata.ConformanceTests;

public sealed class ConformanceCase
{
    public string Name { get; set; } = "";
    public string Request { get; set; } = "";
    public ExpectBlock Expect { get; set; } = new();

    public sealed class ExpectBlock
    {
        public int Status { get; set; } = 200;
        public Dictionary<string, string>? Jsonpath { get; set; }
        public List<string>? BodyContains { get; set; }
        public List<string>? BodyNotContains { get; set; }
        public Dictionary<string, string>? Headers { get; set; }
    }

    public override string ToString() => Name;
}

/// <summary>
/// Table-driven OData conformance suite (spec 13 §3): YAML cases run against the
/// live host + PostgreSQL fixture. Each doc 05 behavior lands here red-first.
/// </summary>
[Collection("conformance")]
public class ConformanceRunner
{
    private readonly ConformanceFixture _fixture;

    public ConformanceRunner(ConformanceFixture fixture) => _fixture = fixture;

    public static TheoryData<string, string> Cases()
    {
        var data = new TheoryData<string, string>();
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();

        var casesDir = Path.Combine(AppContext.BaseDirectory, "cases");
        foreach (var file in Directory.GetFiles(casesDir, "*.yaml").OrderBy(f => f))
        {
            var cases = deserializer.Deserialize<List<ConformanceCase>>(File.ReadAllText(file));
            foreach (var c in cases)
            {
                data.Add(Path.GetFileNameWithoutExtension(file), c.Name);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Conformance(string file, string caseName)
    {
        var testCase = Load(file, caseName);

        var parts = testCase.Request.Split(' ', 2);
        var method = new HttpMethod(parts[0]);
        var url = parts[1].Replace("\n", "").Replace(" ", "%20");
        if (url.StartsWith("/", StringComparison.Ordinal) && !url.StartsWith("/api/", StringComparison.Ordinal))
        {
            // Service-relative cases target the fixture's 'sales' service; absolute
            // paths (starting with /api/) pass through untouched.
            url = $"/api/odata/{ConformanceFixture.ServiceName}{(url == "/" ? "" : url)}";
        }

        using var request = new HttpRequestMessage(method, url);
        using var response = await _fixture.Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.True((int)response.StatusCode == testCase.Expect.Status,
            $"[{caseName}] expected {testCase.Expect.Status}, got {(int)response.StatusCode}. Body: {Truncate(body)}");

        if (testCase.Expect.Headers is { } headers)
        {
            foreach (var header in headers)
            {
                var actual = response.Headers.TryGetValues(header.Key, out var values)
                    ? string.Join(",", values)
                    : response.Content.Headers.TryGetValues(header.Key, out var contentValues)
                        ? string.Join(",", contentValues)
                        : null;
                Assert.True(actual is not null && actual.Contains(header.Value),
                    $"[{caseName}] header {header.Key}: expected to contain '{header.Value}', got '{actual}'.");
            }
        }

        foreach (var fragment in testCase.Expect.BodyContains ?? [])
        {
            Assert.True(body.Contains(fragment, StringComparison.Ordinal),
                $"[{caseName}] body does not contain '{fragment}'. Body: {Truncate(body)}");
        }

        foreach (var fragment in testCase.Expect.BodyNotContains ?? [])
        {
            Assert.False(body.Contains(fragment, StringComparison.Ordinal),
                $"[{caseName}] body must NOT contain '{fragment}'.");
        }

        if (testCase.Expect.Jsonpath is { } pathAssertions)
        {
            using var doc = JsonDocument.Parse(body);
            foreach (var assertion in pathAssertions)
            {
                AssertJsonPath(doc.RootElement, assertion.Key, assertion.Value, caseName, body);
            }
        }
    }

    private static void AssertJsonPath(JsonElement root, string path, string expectation, string caseName, string body)
    {
        var (found, element, length) = JsonPathLite.Evaluate(root, path);

        if (expectation == "absent")
        {
            Assert.False(found, $"[{caseName}] {path} should be absent.");
            return;
        }

        Assert.True(found, $"[{caseName}] {path} not found. Body: {Truncate(body)}");
        if (expectation == "exists") return;

        var actualNumber = length ?? (element.ValueKind == JsonValueKind.Number ? element.GetDouble() : (double?)null);

        // Comparison expectations: ">= 10", "> 0", etc.
        foreach (var op in new[] { ">=", "<=", ">", "<" })
        {
            if (expectation.StartsWith(op, StringComparison.Ordinal))
            {
                var bound = double.Parse(expectation.Substring(op.Length).Trim(), CultureInfo.InvariantCulture);
                Assert.True(actualNumber is not null, $"[{caseName}] {path} is not numeric.");
                var ok = op switch
                {
                    ">=" => actualNumber >= bound,
                    "<=" => actualNumber <= bound,
                    ">" => actualNumber > bound,
                    _ => actualNumber < bound,
                };
                Assert.True(ok, $"[{caseName}] {path}: expected {expectation}, got {actualNumber}.");
                return;
            }
        }

        if (double.TryParse(expectation, NumberStyles.Any, CultureInfo.InvariantCulture, out var expectedNumber))
        {
            Assert.True(actualNumber == expectedNumber,
                $"[{caseName}] {path}: expected {expectedNumber}, got {actualNumber ?? (object)element.ToString()}.");
            return;
        }

        if (expectation is "true" or "false")
        {
            Assert.Equal(expectation == "true", element.GetBoolean());
            return;
        }

        Assert.True(element.ValueKind == JsonValueKind.String && element.GetString() == expectation,
            $"[{caseName}] {path}: expected '{expectation}', got '{element}'.");
    }

    private static ConformanceCase Load(string file, string caseName)
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();
        var cases = deserializer.Deserialize<List<ConformanceCase>>(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "cases", file + ".yaml")));
        return cases.First(c => c.Name == caseName);
    }

    private static string Truncate(string body) => body.Length <= 500 ? body : body.Substring(0, 500) + "…";
}
