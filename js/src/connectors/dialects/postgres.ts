import type { FilterFunction } from "../../core/query.js";
import { NotSupportedQueryError, type ReturningMode, type SqlDialect } from "../contracts.js";

/** PostgreSQL syntax (port of PostgreSqlDialect.cs, spec 04 §7.2 translation column). */
export class PostgreSqlDialect implements SqlDialect {
  readonly caseInsensitiveLike = true; // ILIKE per spec 04 §7.2
  readonly returning: ReturningMode = "returningSuffix";

  quoteIdentifier(identifier: string): string {
    return `"${identifier.replaceAll('"', '""')}"`;
  }

  paginate(sql: string, limit: number | undefined, offset: number | undefined): string {
    let out = sql;
    if (limit !== undefined) out += ` LIMIT ${limit}`;
    if (offset !== undefined && offset > 0) out += ` OFFSET ${offset}`;
    return out;
  }

  mapFunction(fn: FilterFunction, args: readonly string[]): string {
    const a = (i: number): string => args[i]!;
    switch (fn) {
      case "contains":
      case "startsWith":
      case "endsWith":
        return `${a(0)} ILIKE ${a(1)} ESCAPE '\\'`;
      case "toLower":
        return `lower(${a(0)})`;
      case "toUpper":
        return `upper(${a(0)})`;
      case "trim":
        return `trim(${a(0)})`;
      case "length":
        return `length(${a(0)})`;
      case "indexOf":
        return `(position(${a(1)} in ${a(0)}) - 1)`;
      case "substring":
        return args.length === 2 ? `substr(${a(0)}, ${a(1)} + 1)` : `substr(${a(0)}, ${a(1)} + 1, ${a(2)})`;
      case "concat":
        return `(${args.join(" || ")})`;
      case "year":
        return `EXTRACT(YEAR FROM ${a(0)})::int`;
      case "month":
        return `EXTRACT(MONTH FROM ${a(0)})::int`;
      case "day":
        return `EXTRACT(DAY FROM ${a(0)})::int`;
      case "hour":
        return `EXTRACT(HOUR FROM ${a(0)})::int`;
      case "minute":
        return `EXTRACT(MINUTE FROM ${a(0)})::int`;
      case "second":
        return `EXTRACT(SECOND FROM ${a(0)})::int`;
      case "date":
        return `(${a(0)})::date`;
      case "time":
        return `(${a(0)})::time`;
      case "now":
        return "now()";
      case "round":
        return `round(${a(0)})`;
      case "floor":
        return `floor(${a(0)})`;
      case "ceiling":
        return `ceiling(${a(0)})`;
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
        throw new NotSupportedQueryError(`Function '${fn}' is not supported on PostgreSQL.`);
    }
  }
}
