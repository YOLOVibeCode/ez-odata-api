import type { FilterFunction } from "../../core/query.js";
import { NotSupportedQueryError, type ReturningMode, type SqlDialect } from "../contracts.js";

/**
 * SQLite syntax (port of SqliteDialect.cs, spec 04 §7.2).
 * RETURNING requires SQLite 3.35+ (bundled e_sqlite3 qualifies).
 */
export class SqliteDialect implements SqlDialect {
  readonly caseInsensitiveLike = true; // ASCII LIKE is case-insensitive by default
  readonly returning: ReturningMode = "returningSuffix";

  quoteIdentifier(identifier: string): string {
    return `"${identifier.replaceAll('"', '""')}"`;
  }

  paginate(sql: string, limit: number | undefined, offset: number | undefined): string {
    let out = sql;
    if (limit !== undefined) out += ` LIMIT ${limit}`;
    else if (offset !== undefined && offset > 0) out += " LIMIT -1"; // SQLite requires LIMIT before OFFSET
    if (offset !== undefined && offset > 0) out += ` OFFSET ${offset}`;
    return out;
  }

  mapFunction(fn: FilterFunction, args: readonly string[]): string {
    const a = (i: number): string => args[i]!;
    switch (fn) {
      case "contains":
      case "startsWith":
      case "endsWith":
        return `${a(0)} LIKE ${a(1)} ESCAPE '\\'`;
      case "toLower":
        return `lower(${a(0)})`;
      case "toUpper":
        return `upper(${a(0)})`;
      case "trim":
        return `trim(${a(0)})`;
      case "length":
        return `length(${a(0)})`;
      case "indexOf":
        return `(instr(${a(0)}, ${a(1)}) - 1)`;
      case "substring":
        return args.length === 2 ? `substr(${a(0)}, ${a(1)} + 1)` : `substr(${a(0)}, ${a(1)} + 1, ${a(2)})`;
      case "concat":
        return `(${args.join(" || ")})`;
      case "year":
        return `CAST(strftime('%Y', ${a(0)}) AS INTEGER)`;
      case "month":
        return `CAST(strftime('%m', ${a(0)}) AS INTEGER)`;
      case "day":
        return `CAST(strftime('%d', ${a(0)}) AS INTEGER)`;
      case "hour":
        return `CAST(strftime('%H', ${a(0)}) AS INTEGER)`;
      case "minute":
        return `CAST(strftime('%M', ${a(0)}) AS INTEGER)`;
      case "second":
        return `CAST(strftime('%S', ${a(0)}) AS INTEGER)`;
      case "date":
        return `date(${a(0)})`;
      case "time":
        return `time(${a(0)})`;
      case "now":
        return "datetime('now')";
      case "round":
        return `round(${a(0)})`;
      // floor/ceiling: math extension not guaranteed; emulated (spec 04 §7.2)
      case "floor":
        return `(CASE WHEN ${a(0)} = CAST(${a(0)} AS INTEGER) OR ${a(0)} > 0 THEN CAST(${a(0)} AS INTEGER) ELSE CAST(${a(0)} AS INTEGER) - 1 END)`;
      case "ceiling":
        return `(CASE WHEN ${a(0)} = CAST(${a(0)} AS INTEGER) OR ${a(0)} < 0 THEN CAST(${a(0)} AS INTEGER) ELSE CAST(${a(0)} AS INTEGER) + 1 END)`;
      case "add":
        return `(${a(0)} + ${a(1)})`;
      case "sub":
        return `(${a(0)} - ${a(1)})`;
      case "mul":
        return `(${a(0)} * ${a(1)})`;
      case "div":
        return `(${a(0)} / ${a(1)})`;
      case "mod":
        return `(${a(0)} % ${a(1)})`;
      default:
        throw new NotSupportedQueryError(`Function '${fn}' is not supported on SQLite.`);
    }
  }
}
