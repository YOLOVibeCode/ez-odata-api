using System.Reflection;
using NetArchTest.Rules;
using Xunit;

namespace EzOdata.UnitTests.Architecture;

/// <summary>
/// Enforces the dependency rules of spec 02 §3 and the ISP guardrails of spec 13 §8.
/// These tests are the executable form of the architecture documentation.
/// </summary>
public class LayeringTests
{
    private static readonly Assembly Core = typeof(EzOdata.Core.ErrorCodes).Assembly;
    private static readonly Assembly ConnectorAbstractions = typeof(Connectors.Abstractions.ConnectorDescriptor).Assembly;
    private static readonly Assembly Data = typeof(Data.Security.AesGcmEnvelopeProtector).Assembly;

    [Theory]
    [InlineData("Microsoft.AspNetCore")]
    [InlineData("Microsoft.EntityFrameworkCore")]
    [InlineData("Npgsql")]
    [InlineData("MySqlConnector")]
    [InlineData("Microsoft.Data.SqlClient")]
    [InlineData("Microsoft.Data.Sqlite")]
    [InlineData("EzOdata.Data")]
    public void Core_has_no_platform_dependencies(string forbidden)
    {
        var result = Types.InAssembly(Core)
            .ShouldNot().HaveDependencyOn(forbidden)
            .GetResult();

        Assert.True(result.IsSuccessful, FailureList(result));
    }

    [Theory]
    [InlineData("Microsoft.AspNetCore")]
    [InlineData("Microsoft.EntityFrameworkCore")]
    [InlineData("EzOdata.Data")]
    public void Connector_abstractions_have_no_platform_dependencies(string forbidden)
    {
        var result = Types.InAssembly(ConnectorAbstractions)
            .ShouldNot().HaveDependencyOn(forbidden)
            .GetResult();

        Assert.True(result.IsSuccessful, FailureList(result));
    }

    [Fact]
    public void Engine_assemblies_target_netstandard20()
    {
        // The net48 hosting requirement (spec 02 §1.1) is only honored if the engine
        // stays on netstandard2.0. Guard against an accidental TFM bump.
        foreach (var assembly in new[] { Core, ConnectorAbstractions })
        {
            var target = assembly.GetCustomAttribute<System.Runtime.Versioning.TargetFrameworkAttribute>();
            Assert.NotNull(target);
            Assert.StartsWith(".NETStandard,Version=v2.0", target.FrameworkName);
        }
    }

    [Fact]
    public void Data_does_not_depend_on_protocol_layers()
    {
        var result = Types.InAssembly(Data)
            .ShouldNot().HaveDependencyOnAny("EzOdata.OData", "EzOdata.Rest", "EzOdata.Mcp", "EzOdata.Admin")
            .GetResult();

        Assert.True(result.IsSuccessful, FailureList(result));
    }

    [Fact]
    public void Interfaces_in_engine_assemblies_stay_small_per_isp()
    {
        // Spec 13 §8: no interface in Core/Connectors.Abstractions may exceed 5 members without an ADR.
        // Property accessors are special-name methods; count each property once.
        var offenders = new[] { Core, ConnectorAbstractions }
            .SelectMany(a => a.GetTypes())
            .Where(t => t.IsInterface
                        && t.GetMethods().Count(m => !m.IsSpecialName) + t.GetProperties().Length > 5)
            .Select(t => t.FullName)
            .ToList();

        Assert.True(offenders.Count == 0,
            "Interfaces exceeding 5 members (ISP, spec 02 §3.1): " + string.Join(", ", offenders));
    }

    private static string FailureList(TestResult result) =>
        result.IsSuccessful ? "" : "Violations: " + string.Join(", ", result.FailingTypes?.Select(t => t.FullName ?? "?") ?? []);
}
