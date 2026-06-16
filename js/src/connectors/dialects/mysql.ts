import type { FilterFunction } from "../../core/query.js";
import { NotSupportedQueryError, type ReturningMode, type SqlDialect } from "../contracts.js";

/** MySQL/MariaDB syntax (port of MySqlDialect.cs, spec 04 §7.2). */
export class MySqlDialect implements SqlDialect {
  readonly caseInsensitiveLike = true; // default *_ci collations
  readonly returning: ReturningMode = "none"; // LAST_INSERT_ID strategy

  quoteIdentifier(identifier: string): string {
    return "`" + identifier.replaceAll("`", "``") + "`";
  }

  paginate(sql: string, limit: number | undefined, offset: number | undefined): string {
    let out = sql;
    if (limit !== undefined) out += ` LIMIT ${limit}`;
    else if (offset !== undefined && offset > 0) out += " LIMIT 18446744073709551615"; // MySQL requires LIMIT with OFFSET
    if (offset !== undefined && offset > 0) out += ` OFFSET ${offset}`;
    return out;
  }

  mapFunction(fn: FilterFunction, args: readonly string[]): string {
    const a = (i: number): string => args[i]!;
    switch (fn) {
      case "contains":
      case "startsWith":
      case "endsWith":
        return `${a(0)} LIKE ${a(1)} ESCAPE '\\\\'`;
      case "toLower":
        return `LOWER(${a(0)})`;
      case "toUpper":
        return `UPPER(${a(0)})`;
      case "trim":
        return `TRIM(${a(0)})`;
      case "length":
        return `CHAR_LENGTH(${a(0)})`;
      case "indexOf":
        return `(LOCATE(${a(1)}, ${a(0)}) - 1)`;
      case "substring":
        return args.length === 2
          ? `SUBSTRING(${a(0)}, ${a(1)} + 1)`
          : `SUBSTRING(${a(0)}, ${a(1)} + 1, ${a(2)})`;
      case "concat":
        return `CONCAT(${args.join(", ")})`;
      case "year":
        return `YEAR(${a(0)})`;
      case "month":
        return `MONTH(${a(0)})`;
      case "day":
        return `DAY(${a(0)})`;
      case "hour":
        return `HOUR(${a(0)})`;
      case "minute":
        return `MINUTE(${a(0)})`;
      case "second":
        return `SECOND(${a(0)})`;
      case "date":
        return `DATE(${a(0)})`;
      case "time":
        return `TIME(${a(0)})`;
      case "now":
        return "NOW()";
      case "round":
        return `ROUND(${a(0)})`;
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
        throw new NotSupportedQueryError(`Function '${fn}' is not supported on MySQL.`);
    }
  }
}
