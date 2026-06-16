import { Row } from "../core/result.js";

/**
 * OData JSON payload builder (port of ODataPayloadWriter.cs, spec 05 §5):
 * shapes query results into the OData minimal-metadata JSON response format.
 */

export interface CollectionPayload {
  "@odata.context": string;
  "@odata.count"?: number;
  "@odata.nextLink"?: string;
  value: Record<string, unknown>[];
}

export interface SinglePayload {
  "@odata.context": string;
  [key: string]: unknown;
}

export function buildCollectionPayload(
  serviceRoot: string,
  tableName: string,
  rows: readonly Row[],
  opts: {
    count?: number;
    nextLink?: string;
  } = {},
): CollectionPayload {
  const context = buildContextUrl(serviceRoot, tableName);
  const result: CollectionPayload = {
    "@odata.context": context,
    value: rows.map((r) => rowToObject(r)),
  };

  if (opts.count !== undefined) result["@odata.count"] = opts.count;
  if (opts.nextLink !== undefined) result["@odata.nextLink"] = opts.nextLink;
  return result;
}

export function buildSinglePayload(
  serviceRoot: string,
  tableName: string,
  row: Row,
): SinglePayload {
  const context = buildContextUrl(serviceRoot, `${tableName}/$entity`);
  return {
    "@odata.context": context,
    ...rowToObject(row),
  };
}

export function buildCountPayload(count: number): string {
  return String(count);
}

/** OData error payload: {"error": {"code": "...", "message": "..."}}. */
export function buildErrorPayload(code: string, message: string): Record<string, unknown> {
  return { error: { code, message } };
}

/** OData $apply result (dynamic/open entities — no type annotation). */
export function buildApplyPayload(
  serviceRoot: string,
  tableName: string,
  rows: readonly Row[],
  groupBy: readonly string[],
  aliases: readonly string[],
): CollectionPayload {
  const context = buildContextUrl(serviceRoot, `${tableName}/$entity`);
  return {
    "@odata.context": context,
    value: rows.map((row) => {
      const obj: Record<string, unknown> = {};
      for (const field of groupBy) obj[field] = normalizeValue(row.get(field));
      for (const alias of aliases) obj[alias] = normalizeValue(row.get(alias));
      return obj;
    }),
  };
}

/** OData service document (spec §3). */
export function buildServiceDocument(
  serviceRoot: string,
  tableNames: readonly string[],
): Record<string, unknown> {
  return {
    "@odata.context": `${trimSlash(serviceRoot)}/$metadata`,
    value: tableNames.map((name) => ({ name, kind: "EntitySet", url: name })),
  };
}

// ---- Helpers ----

function rowToObject(row: Row): Record<string, unknown> {
  const obj: Record<string, unknown> = {};
  for (const [key, val] of row.values) {
    if (val instanceof Row) {
      obj[key] = rowToObject(val);
    } else if (Array.isArray(val) && val.length > 0 && val[0] instanceof Row) {
      obj[key] = (val as Row[]).map(rowToObject);
    } else {
      obj[key] = normalizeValue(val);
    }
  }
  return obj;
}

function normalizeValue(val: unknown): unknown {
  if (val === undefined || val === null) return null;
  if (typeof val === "bigint") return Number(val);
  // Convert Date objects to ISO string
  if (val instanceof Date) return val.toISOString();
  // Convert Buffer / Uint8Array to base64 for binary columns
  if (Buffer.isBuffer(val)) return val.toString("base64");
  return val;
}

function buildContextUrl(serviceRoot: string, path: string): string {
  return `${trimSlash(serviceRoot)}/$metadata#${path}`;
}

function trimSlash(s: string): string {
  return s.endsWith("/") ? s.slice(0, -1) : s;
}
