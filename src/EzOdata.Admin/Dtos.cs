using System.Text.Json;
using System.Text.Json.Serialization;
using EzOdata.Core.Services;
using EzOdata.Data.Entities;

namespace EzOdata.Admin;

// ---- Setup / Auth ----

public sealed record SetupStatusResponse(bool Required);

public sealed record SetupRequest(string Email, string DisplayName, string Password);

public sealed record LoginRequest(string Email, string Password);

public sealed record RefreshRequest(string RefreshToken);

public sealed record AuthResponse(
    string AccessToken,
    DateTimeOffset ExpiresAt,
    string RefreshToken,
    UserResponse User);

public sealed record UserResponse(long Id, string Email, string DisplayName, bool IsSystemAdmin, IReadOnlyList<string> Roles)
{
    public static UserResponse From(UserEntity user, IEnumerable<RoleEntity> roles) =>
        new(user.Id, user.Email, user.DisplayName, user.IsSystemAdmin,
            roles.Where(r => r.IsActive).Select(r => r.Name).ToList());
}

// ---- Services ----

public sealed record ConnectionInput
{
    public string? Host { get; init; }
    public int? Port { get; init; }
    public string? Database { get; init; }
    public string? Username { get; init; }
    public string? Password { get; init; }
    public string? FilePath { get; init; }
    public TlsInput? Tls { get; init; }
    public Dictionary<string, string>? Extra { get; init; }
}

public sealed record TlsInput(string? Mode, string? CaCertPem, bool? AllowInvalid);

public sealed record CreateServiceRequest(
    string Name,
    string Label,
    string? Description,
    string ConnectorType,
    ConnectionInput Connection,
    ServiceOptions? Options);

public sealed record UpdateServiceRequest(
    string? Label,
    string? Description,
    ConnectionInput? Connection,
    ServiceOptions? Options,
    int? SchemaRefreshMinutes);

/// <summary>Connection echo is display-safe only — never credentials (spec 07 §4).</summary>
public sealed record ServiceResponse(
    long Id,
    string Name,
    string Label,
    string? Description,
    string ConnectorType,
    string Connection,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] ServiceStatus Status,
    string? StatusDetail,
    ServiceOptions Options,
    int? SchemaRefreshMinutes,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    long RowVersion)
{
    public static ServiceResponse From(ServiceEntity e) =>
        new(e.Id, e.Name, e.Label, e.Description, e.ConnectorType, e.ConnectionDisplay,
            e.Status, e.StatusDetail,
            JsonSerializer.Deserialize<ServiceOptions>(e.OptionsJson, JsonDefaults.Options) ?? new ServiceOptions(),
            e.SchemaRefreshMinutes, e.CreatedAt, e.UpdatedAt, e.RowVersion);
}

public sealed record ConnectorInfoResponse(string Type, string DisplayName);

public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}
