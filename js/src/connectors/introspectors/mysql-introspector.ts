import type { SchemaIntrospector, ConnectionSpec, IntrospectionOptions } from "../contracts.js";
import type { ColumnModel, ForeignKeyModel, SchemaSnapshot, TableModel } from "../../core/schema.js";
import { createKnex, extractRawRows } from "../knex-helpers.js";
import { toOneName, toManyName } from "../navigation-naming.js";

interface MapTypeResult {
  edmType: string;
  isFallback: boolean;
  allowedValues?: string[];
}

/**
 * information_schema-based introspection for MySQL/MariaDB (port of MySqlIntrospector.cs, spec 04 §4).
 */
export class MySqlIntrospector implements SchemaIntrospector {
  /** MySQL DATA_TYPE + COLUMN_TYPE → EDM type (port of MySqlIntrospector.MapType). */
  static mapType(dataType: string, columnType: string): MapTypeResult {
    if (columnType.toLowerCase() === "tinyint(1)") {
      return { edmType: "Edm.Boolean", isFallback: false };
    }
    const unsigned = columnType.toLowerCase().includes("unsigned");

    if (dataType.toLowerCase() === "enum") {
      const inner = columnType.substring(columnType.indexOf("(") + 1).replace(/\)$/, "");
      const values = inner.split(",").map((v) => v.trim().replace(/^'|'$/g, "").replaceAll("''", "'"));
      return { edmType: "Edm.String", isFallback: false, allowedValues: values };
    }

    switch (dataType.toLowerCase()) {
      case "tinyint":
        return { edmType: "Edm.Int16", isFallback: false };
      case "smallint":
        return { edmType: unsigned ? "Edm.Int32" : "Edm.Int16", isFallback: false };
      case "mediumint":
      case "int":
      case "integer":
        return { edmType: unsigned ? "Edm.Int64" : "Edm.Int32", isFallback: false };
      case "bigint":
        return { edmType: "Edm.Int64", isFallback: false };
      case "decimal":
      case "numeric":
        return { edmType: "Edm.Decimal", isFallback: false };
      case "float":
        return { edmType: "Edm.Single", isFallback: false };
      case "double":
      case "real":
        return { edmType: "Edm.Double", isFallback: false };
      case "bit":
        return { edmType: "Edm.Boolean", isFallback: false };
      case "char":
      case "varchar":
      case "tinytext":
      case "text":
      case "mediumtext":
      case "longtext":
        return { edmType: "Edm.String", isFallback: false };
      case "datetime":
      case "timestamp":
        return { edmType: "Edm.DateTimeOffset", isFallback: false };
      case "date":
        return { edmType: "Edm.Date", isFallback: false };
      case "time":
        return { edmType: "Edm.TimeOfDay", isFallback: false };
      case "year":
        return { edmType: "Edm.Int16", isFallback: false };
      case "binary":
      case "varbinary":
      case "tinyblob":
      case "blob":
      case "mediumblob":
      case "longblob":
        return { edmType: "Edm.Binary", isFallback: false };
      case "json":
        return { edmType: "Edm.Untyped", isFallback: false };
      default:
        return { edmType: "Edm.String", isFallback: true };
    }
  }

  async introspect(spec: ConnectionSpec, options: IntrospectionOptions, _signal?: AbortSignal): Promise<SchemaSnapshot> {
    const db = createKnex(spec, "mysql2");
    try {
      const database = spec.database!;

      const tablesResult = await db.raw(
        `SELECT TABLE_NAME AS name, TABLE_TYPE AS ttype, TABLE_COMMENT AS comment
         FROM information_schema.TABLES WHERE TABLE_SCHEMA = ? ORDER BY TABLE_NAME`,
        [database],
      );
      const rawTables = extractRawRows(tablesResult, "mysql2").map((r) => ({
        name: r.name as string,
        isView: (r.ttype as string) === "VIEW",
        comment: r.comment as string | null,
      }));

      const included = rawTables
        .filter((t) => options.excludeTables.length === 0 || !options.excludeTables.some((p) => matchGlob(t.name, p)))
        .filter((t) => options.includeViews || !t.isView);
      const includedNames = new Set(included.map((t) => t.name));

      const colResult = await db.raw(
        `SELECT TABLE_NAME AS tbl, COLUMN_NAME AS col, ORDINAL_POSITION AS ord,
                DATA_TYPE AS dt, COLUMN_TYPE AS ct,
                CASE WHEN IS_NULLABLE = 'YES' THEN 1 ELSE 0 END AS nullable,
                CHARACTER_MAXIMUM_LENGTH AS max_len,
                NUMERIC_PRECISION AS prec, NUMERIC_SCALE AS scale,
                COLUMN_DEFAULT AS dflt, EXTRA AS extra, COLUMN_COMMENT AS comment, COLUMN_KEY AS col_key
         FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = ? ORDER BY TABLE_NAME, ORDINAL_POSITION`,
        [database],
      );
      const columnsByTable = new Map<string, ColumnModel[]>();
      for (const r of extractRawRows(colResult, "mysql2")) {
        const tableName = r.tbl as string;
        if (!includedNames.has(tableName)) continue;
        const extra = (r.extra as string | null) ?? "";
        const { edmType, isFallback, allowedValues } = MySqlIntrospector.mapType(r.dt as string, r.ct as string);
        if (!columnsByTable.has(tableName)) columnsByTable.set(tableName, []);
        const maxLen = r.max_len as number | bigint | null;
        const myCol: ColumnModel = {
          dbName: r.col as string,
          exposedName: r.col as string,
          dbType: r.ct as string,
          edmType,
          isFallbackType: isFallback,
          nullable: (r.nullable as number) === 1,
          isAutoGenerated: extra.toLowerCase().includes("auto_increment"),
          isComputed: extra.toUpperCase().includes("GENERATED"),
          isPrimaryKey: false,
        };
        if (allowedValues !== undefined) (myCol as unknown as Record<string, unknown>)["allowedValues"] = allowedValues;
        if (maxLen !== null && !(typeof maxLen === "bigint" && maxLen > 2147483647n)) {
          (myCol as unknown as Record<string, unknown>)["maxLength"] = Number(maxLen);
        }
        if (r.prec != null) (myCol as unknown as Record<string, unknown>)["precision"] = Number(r.prec);
        if (r.scale != null) (myCol as unknown as Record<string, unknown>)["scale"] = Number(r.scale);
        const dflt = r.dflt as string | null;
        if (dflt !== null) (myCol as unknown as Record<string, unknown>)["defaultExpression"] = dflt;
        const cmt = r.comment as string | null;
        if (cmt) (myCol as unknown as Record<string, unknown>)["comment"] = cmt;
        columnsByTable.get(tableName)!.push(myCol);
      }

      const keyResult = await db.raw(
        `SELECT tc.TABLE_NAME AS tbl, tc.CONSTRAINT_NAME AS con, tc.CONSTRAINT_TYPE AS ctype,
                kcu.COLUMN_NAME AS col, kcu.ORDINAL_POSITION AS ord
         FROM information_schema.TABLE_CONSTRAINTS tc
         JOIN information_schema.KEY_COLUMN_USAGE kcu
           ON kcu.CONSTRAINT_NAME = tc.CONSTRAINT_NAME
          AND kcu.TABLE_SCHEMA = tc.TABLE_SCHEMA
          AND kcu.TABLE_NAME = tc.TABLE_NAME
         WHERE tc.TABLE_SCHEMA = ? AND tc.CONSTRAINT_TYPE IN ('PRIMARY KEY', 'UNIQUE')
         ORDER BY tc.TABLE_NAME, tc.CONSTRAINT_NAME, kcu.ORDINAL_POSITION`,
        [database],
      );
      const keysByTable = new Map<string, Array<{ con: string; isPrimary: boolean; col: string; ord: number }>>();
      for (const r of extractRawRows(keyResult, "mysql2")) {
        const tbl = r.tbl as string;
        if (!includedNames.has(tbl)) continue;
        if (!keysByTable.has(tbl)) keysByTable.set(tbl, []);
        keysByTable.get(tbl)!.push({ con: r.con as string, isPrimary: (r.ctype as string) === "PRIMARY KEY", col: r.col as string, ord: r.ord as number });
      }

      const fkResult = await db.raw(
        `SELECT CONSTRAINT_NAME AS con, TABLE_NAME AS tbl, COLUMN_NAME AS col,
                REFERENCED_TABLE_NAME AS ref_tbl, REFERENCED_COLUMN_NAME AS ref_col,
                ORDINAL_POSITION AS ord
         FROM information_schema.KEY_COLUMN_USAGE
         WHERE TABLE_SCHEMA = ? AND REFERENCED_TABLE_NAME IS NOT NULL
         ORDER BY CONSTRAINT_NAME, ORDINAL_POSITION`,
        [database],
      );
      const fksRaw = extractRawRows(fkResult, "mysql2").map((r) => ({
        con: r.con as string,
        tbl: r.tbl as string,
        col: r.col as string,
        refTbl: r.ref_tbl as string,
        refCol: r.ref_col as string,
        ord: r.ord as number,
      }));

      const tables: TableModel[] = [];
      for (const { name, isView, comment } of included) {
        const keys = keysByTable.get(name) ?? [];
        const pkCols = keys.filter((k) => k.isPrimary).sort((a, b) => a.ord - b.ord).map((k) => k.col);
        const pkSet = new Set(pkCols);
        const columns = (columnsByTable.get(name) ?? []).map((c) =>
          pkSet.has(c.exposedName) ? { ...c, isPrimaryKey: true, nullable: false } : c,
        );
        const uniqueGroups = groupBy(keys.filter((k) => !k.isPrimary), (k) => k.con);
        const uniques = Object.values(uniqueGroups).map((g) =>
          g.sort((a, b) => a.ord - b.ord).map((k) => k.col),
        );

        const myTable: TableModel = {
          dbSchema: "",
          dbName: name,
          exposedName: name,
          isView,
          writable: !isView,
          columns,
          primaryKey: pkCols,
          uniqueConstraints: uniques,
          foreignKeys: [],
        };
        if (comment) (myTable as unknown as Record<string, unknown>)["comment"] = comment;
        tables.push(myTable);
      }

      // Attach FKs
      for (let i = 0; i < tables.length; i++) {
        const t = tables[i]!;
        const childTaken = new Set(t.columns.map((c) => c.exposedName));
        const fks: ForeignKeyModel[] = [];
        const grouped = groupBy(fksRaw.filter((f) => f.tbl === t.dbName), (f) => f.con);
        for (const [conName, group] of Object.entries(grouped).sort(([a], [b]) => a.localeCompare(b))) {
          const refTbl = group[0]!.refTbl;
          if (!includedNames.has(refTbl)) continue;
          const parent = tables.find((p) => p.exposedName === refTbl)!;
          const parentTaken = new Set([...parent.columns.map((c) => c.exposedName), ...parent.foreignKeys.map((f) => f.navToMany)]);
          const ordered = [...group].sort((a, b) => a.ord - b.ord);
          const fromColumns = ordered.map((g) => g.col);
          fks.push({
            name: conName,
            columns: fromColumns,
            refTable: refTbl,
            refColumns: ordered.map((g) => g.refCol),
            navToOne: toOneName(fromColumns, refTbl, conName, childTaken),
            navToMany: toManyName(t.exposedName, conName, parentTaken),
          });
        }
        tables[i] = { ...t, foreignKeys: fks };
      }

      return {
        version: 1,
        engine: "mysql",
        collectedAt: new Date().toISOString(),
        tables: [...tables].sort((a, b) => a.exposedName.localeCompare(b.exposedName)),
      };
    } finally {
      await db.destroy();
    }
  }
}

function matchGlob(value: string, pattern: string): boolean {
  const re = new RegExp("^" + pattern.replace(/\*/g, ".*").replace(/\?/g, ".") + "$", "i");
  return re.test(value);
}

function groupBy<T>(arr: T[], key: (item: T) => string): Record<string, T[]> {
  const result: Record<string, T[]> = {};
  for (const item of arr) {
    const k = key(item);
    if (!result[k]) result[k] = [];
    result[k]!.push(item);
  }
  return result;
}
