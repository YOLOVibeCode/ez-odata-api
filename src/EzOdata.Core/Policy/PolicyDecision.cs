using EzOdata.Core.Query;

namespace EzOdata.Core.Policy;

/// <summary>
/// Outcome of authorization for one (identity, service, table, verb) — spec 08 §4.
/// </summary>
public sealed record PolicyDecision
{
    public bool Allowed { get; init; }

    /// <summary>True ⇒ respond 404, not 403: the resource is invisible to this identity (spec 08 §5.1).</summary>
    public bool Hidden { get; init; }

    public string? DenialCode { get; init; }
    public string? DenialMessage { get; init; }

    /// <summary>True ⇒ superuser data access; no rewriting applied (audited with bypass flag).</summary>
    public bool Bypass { get; init; }

    /// <summary>Fields that must never be readable, filterable, sortable, or writable.</summary>
    public IReadOnlyCollection<string> DeniedFields { get; init; } = [];

    /// <summary>Field → mask literal; returned masked, not filterable/sortable/writable.</summary>
    public IReadOnlyDictionary<string, string> MaskedFields { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Accepted on writes, never returned/filterable.</summary>
    public IReadOnlyCollection<string> WriteOnlyFields { get; init; } = [];

    /// <summary>Combined row filter (already claim-substituted and parsed), AND-ed into every operation.</summary>
    public FilterNode? RowFilter { get; init; }

    public static PolicyDecision Deny(string code, string message, bool hidden = false) =>
        new() { Allowed = false, Hidden = hidden, DenialCode = code, DenialMessage = message };

    public static readonly PolicyDecision FullAccess = new() { Allowed = true, Bypass = true };
}
