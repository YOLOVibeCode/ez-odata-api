namespace EzOdata.Connectors.Abstractions;

/// <summary>
/// Segregated capability (spec 02 §3.1, 04 §2): connection testing only.
/// Must return within 10 seconds (CON-5); implementations enforce their own timeout.
/// </summary>
public interface IConnectionTester
{
    Task<ConnectionTestResult> TestAsync(ConnectionSpec spec, CancellationToken ct);
}
