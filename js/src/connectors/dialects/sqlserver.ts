import type { FilterFunction } from "../../core/query.js";
import { NotSupportedQueryError, type ReturningMode, type SqlDialect } from "../contracts.js";

/**
 * SQL Server syntax (port of SqlServerDialect.cs, spec 04 §7.2).
 * Pagination is OFFSET..FETCH (requires ORDER BY, which the shared compiler
 * always emits via PK tiebreakers).
 */
export class SqlServerDialect implements SqlDialect {
  readonly caseInsensitiveLike = true; // default CI collations
  readonly returning: ReturningMode = "outputClause";

  quoteIdentifier(identifier: string): string {
    return "[" + identifier.replaceAll("]", "]]") + "]";
  }

  paginate(sql: string, limit: number | undefined, offset: number | undefined): string {
    if (limit === undefined && (offset === undefined || offset <= 0)) return sql;
    let out = sql + ` OFFSET ${offset ?? 0} ROWS`;
    if (limit !== undefined) out += ` FETCH NEXT ${limit} ROWS ONLY`;
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
        return `LOWER(${a(0)})`;
      case "toUpper":
        return `UPPER(${a(0)})`;
      case "trim":
        return `TRIM(${a(0)})`;
      case "length":
        return `LEN(${a(0)})`;
      case "indexOf":
        return `(CHARINDEX(${a(1)}, ${a(0)}) - 1)`;
      case "substring":
        return args.length === 2
          ? `SUBSTRING(${a(0)}, ${a(1)} + 1, LEN(${a(0)}))`
          : `SUBSTRING(${a(0)}, ${a(1)} + 1, ${a(2)})`;
      case "concat":
        return `CONCAT(${args.join(", ")})`;
      case "year":
        return `DATEPART(year, ${a(0)})`;
      case "month":
        return `DATEPART(month, ${a(0)})`;
      case "day":
        return `DATEPART(day, ${a(0)})`;
      case "hour":
        return `DATEPART(hour, ${a(0)})`;
      case "minute":
        return `DATEPART(minute, ${a(0)})`;
      case "second":
        return `DATEPART(second, ${a(0)})`;
      case "date":
        return `CAST(${a(0)} AS date)`;
      case "time":
        return `CAST(${a(0)} AS time)`;
      case "now":
        return "SYSDATETIMEOFFSET()";
      case "round":
        return `ROUND(${a(0)}, 0)`;
      case "floor":
        return `FLOOR(${a(0)})`;
      case "ceiling":
        return `CEILING(${a(0)})`;
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
        throw new NotSupportedQueryError(`Function '${fn}' is not supported on SQL Server.`);
    }
  }
}
