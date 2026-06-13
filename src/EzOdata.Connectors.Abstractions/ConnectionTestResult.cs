namespace EzOdata.Connectors.Abstractions;

/// <summary>Structured connection test outcome (spec 04 CON-5).</summary>
public sealed record ConnectionTestResult(
    bool Ok,
    ConnectionTestCategory Category,
    string Message,
    string? ServerVersion = null)
{
    public static ConnectionTestResult Success(string? serverVersion) =>
        new(true, ConnectionTestCategory.Ok, "Connection succeeded.", serverVersion);

    public static ConnectionTestResult Failure(ConnectionTestCategory category, string message) =>
        new(false, category, message);
}

public enum ConnectionTestCategory
{
    Ok = 0,
    AuthFailed = 1,
    Unreachable = 2,
    TlsError = 3,
    DatabaseMissing = 4,
    Other = 5,
}
