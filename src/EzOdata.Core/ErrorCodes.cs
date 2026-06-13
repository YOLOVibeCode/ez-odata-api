namespace EzOdata.Core;

/// <summary>
/// Stable, documented error codes returned to clients (spec 02 §9).
/// These strings are part of the public API contract; never change existing values.
/// </summary>
public static class ErrorCodes
{
    public const string ValidationUnknownProperty = "Validation.UnknownProperty";
    public const string ValidationBadFilter = "Validation.BadFilter";
    public const string ValidationNotNullViolation = "Validation.NotNullViolation";
    public const string ValidationValueTooLong = "Validation.ValueTooLong";
    public const string ValidationInvalidValue = "Validation.InvalidValue";

    public const string ForbiddenVerb = "Forbidden.Verb";
    public const string ForbiddenFieldDenied = "Forbidden.FieldDenied";
    public const string ForbiddenExpandDenied = "Forbidden.ExpandDenied";
    public const string ForbiddenRowFilter = "Forbidden.RowFilter";

    public const string ConflictUniqueViolation = "Conflict.UniqueViolation";
    public const string ConflictForeignKeyViolation = "Conflict.ForeignKeyViolation";

    public const string UpstreamUnavailable = "Upstream.Unavailable";
    public const string UpstreamTimeout = "Upstream.Timeout";
    public const string UpstreamPermissionDenied = "Upstream.PermissionDenied";

    public const string RateLimited = "RateLimited";
    public const string InternalUnmapped = "Internal.Unmapped";
}
