/**
 * Stable, documented error codes returned to clients (port of
 * src/EzOdata.Core/ErrorCodes.cs, spec 02 §9). These strings are part of the
 * public API contract; never change existing values.
 */
export const ErrorCodes = {
  ValidationUnknownProperty: "Validation.UnknownProperty",
  ValidationBadFilter: "Validation.BadFilter",
  ValidationNotNullViolation: "Validation.NotNullViolation",
  ValidationValueTooLong: "Validation.ValueTooLong",
  ValidationInvalidValue: "Validation.InvalidValue",

  ForbiddenVerb: "Forbidden.Verb",
  ForbiddenFieldDenied: "Forbidden.FieldDenied",
  ForbiddenExpandDenied: "Forbidden.ExpandDenied",
  ForbiddenRowFilter: "Forbidden.RowFilter",

  ConflictUniqueViolation: "Conflict.UniqueViolation",
  ConflictForeignKeyViolation: "Conflict.ForeignKeyViolation",

  UpstreamUnavailable: "Upstream.Unavailable",
  UpstreamTimeout: "Upstream.Timeout",
  UpstreamPermissionDenied: "Upstream.PermissionDenied",

  RateLimited: "RateLimited",
  InternalUnmapped: "Internal.Unmapped",
} as const;

export type ErrorCode = (typeof ErrorCodes)[keyof typeof ErrorCodes];

/**
 * An error carrying a stable code + HTTP status, surfaced to clients in the
 * OData/REST error envelope.
 */
export class EzError extends Error {
  readonly code: string;
  readonly status: number;

  constructor(code: string, message: string, status = 400) {
    super(message);
    this.name = "EzError";
    this.code = code;
    this.status = status;
  }
}

/**
 * Thrown when a role's row-filter expression cannot be parsed or a referenced
 * identity claim is missing - the policy engine fails closed (spec 08 §5.4).
 * Port of RowFilterException.
 */
export class RowFilterError extends Error {
  constructor(message: string, options?: { cause?: unknown }) {
    super(message, options);
    this.name = "RowFilterError";
  }
}
