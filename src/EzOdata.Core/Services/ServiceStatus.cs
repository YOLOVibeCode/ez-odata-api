namespace EzOdata.Core.Services;

/// <summary>Schema cache / service lifecycle states (spec 02 §7).</summary>
public enum ServiceStatus
{
    Pending = 0,
    Introspecting = 1,
    Active = 2,
    Failed = 3,
    Refreshing = 4,
    Disabled = 5,
}
