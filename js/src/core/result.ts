import type { KeysetCursor } from "./query.js";

/**
 * Engine-agnostic result rows (port of src/EzOdata.Core/Query/QueryResult.cs):
 * ordered field/value maps, already shaped by projection. Expanded navigations
 * appear as nested Row lists (to-many) or a single Row (to-one).
 */
export class Row {
  private readonly map: Map<string, unknown>;

  constructor(values?: Iterable<readonly [string, unknown]>) {
    this.map = new Map(values);
  }

  get values(): ReadonlyMap<string, unknown> {
    return this.map;
  }

  get(field: string): unknown {
    return this.map.has(field) ? this.map.get(field) : null;
  }

  has(field: string): boolean {
    return this.map.has(field);
  }

  set(field: string, value: unknown): void {
    this.map.set(field, value);
  }

  remove(field: string): boolean {
    return this.map.delete(field);
  }
}

export interface QueryResult {
  readonly rows: readonly Row[];
  readonly hasMore: boolean;
  readonly nextCursor?: KeysetCursor;
}
