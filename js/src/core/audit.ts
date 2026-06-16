/**
 * Write-only audit contract (ISP, spec 02 §3.1, port of
 * src/EzOdata.Core/Audit/IAuditSink.cs): querying is a separate, admin-only
 * concern. Implementations must never block the data path (spec 08 §8 / NFR-8).
 */
export interface AuditSink {
  /** Enqueue an event; drops (with a counter) rather than blocking on overflow. */
  record(event: AuditEvent): void;
}

/** One audit event (spec 03 §2.12). Never contains row data or credentials. */
export interface AuditEvent {
  readonly occurredAt: string;
  readonly requestId: string;
  /** "data.read" | "data.write" | "auth" | "admin" | "mcp" | "system". */
  readonly category: string;
  readonly action: string;
  /** "ok" | "denied" | "error". */
  readonly outcome: string;
  readonly serviceId?: number;
  readonly appId?: number;
  readonly userId?: number;
  readonly roleId?: number;
  readonly resource?: string;
  readonly detailJson?: string;
  readonly durationMs?: number;
}
