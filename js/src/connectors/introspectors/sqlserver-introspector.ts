import type { SchemaIntrospector, ConnectionSpec, IntrospectionOptions } from "../contracts.js";
import type { ColumnModel, ForeignKeyModel, SchemaSnapshot, TableModel } from "../../core/schema.js";
import { createKnex, extractRawRows } from "../knex-helpers.js";
import { toOneName, toManyName } from "../navigation-naming.js";

/**
 * INFORMATION_SCHEMA + sys catalog introspection for SQL Server
 * (port of SqlServerIntrospector.cs, spec 04 §4).
 */
export class SqlServerIntrospector implements SchemaIntrospector {
  /** SQL Server DATA_TYPE → EDM type (port of SqlServerIntrospector.MapType). */
  static mapType(dataType: string): { edmType: string; isFallback: boolean } {
    switch (dataType.toLowerCase()) {
      case "tinyint":
      case "smallint":
        return { edmType: "Edm.Int16", isFallback: false };
      case "int":
        return { edmType: "Edm.Int32", isFallback: false };
      case "bigint":
        return { edmType: "Edm.Int64", isFallback: false };
      case "decimal":
      case "numeric":
      case "money":
      case "smallmoney":
        return { edmType: "Edm.Decimal", isFallback: false };
      case "float":
        return { edmType: "Edm.Double", isFallback: false };
      case "real":
        return { edmType: "Edm.Single", isFallback: false };
      case "bit":
        return { edmType: "Edm.Boolean", isFallback: false };
      case "char":
      case "varchar":
      case "text":
      case "nchar":
      case "nvarchar":
      case "ntext":
        return { edmType: "Edm.String", isFallback: false };
      case "uniqueidentifier":
        return { edmType: "Edm.Guid", isFallback: false };
      case "datetime2":
      case "datetimeoffset":
      case "datetime":
      case "smalldatetime":
        return { edmType: "Edm.DateTimeOffset", isFallback: false };
      case "date":
        return { edmType: "Edm.Date", isFallback: false };
      case "time":
        return { edmType: "Edm.TimeOfDay", isFallback: false };
      case "binary":
      case "varbinary":
      case "image":
      case "rowversion":
      case "timestamp":
        return { edmType: "Edm.Binary", isFallback: false };
      default:
        return { edmType: "Edm.String", isFallback: true };
    }
  }

  async introspect(spec: ConnectionSpec, options: IntrospectionOptions, _signal?: AbortSignal): Promise<SchemaSnapshot> {
    const db = createKnex(spec, "mssql");
    try {
      const tablesResult = await db.raw(
        `SELECT s.name AS schema_name, o.name AS obj_name, o.type AS obj_type,
                CAST(ep.value AS nvarchar(max)) AS comment
         FROM sys.objects o
         JOIN sys.schemas s ON s.schema_id = o.schema_id
         LEFT JOIN sys.extended_properties ep
           ON ep.major_id = o.object_id AND ep.minor_id = 0 AND ep.name = 'MS_Description'
         WHERE o.type IN ('U', 'V')
         ORDER BY s.name, o.name`,
      );
      const rawTables = extractRawRows(tablesResult, "mssql").map((r) => ({
        schema: r.schema_name as string,
        name: r.obj_name as string,
        isView: (r.obj_type as string).trim() === "V",
        comment: r.comment as string | null,
      }));

      const included = rawTables
        .filter((t) => options.includeSchemas.length === 0 || options.includeSchemas.some((p) => matchGlob(t.schema, p)))
        .filter((t) => options.excludeTables.length === 0 || !options.excludeTables.some((p) => matchGlob(t.name, p)))
        .filter((t) => options.includeViews || !t.isView);
      const includedKeys = new Set(included.map((t) => `${t.schema}:${t.name}`));

      const colResult = await db.raw(
        `SELECT c.TABLE_SCHEMA AS schema_name, c.TABLE_NAME AS tbl, c.COLUMN_NAME AS col,
                c.ORDINAL_POSITION AS ord, c.DATA_TYPE AS dt,
                CASE WHEN c.IS_NULLABLE = 'YES' THEN 1 ELSE 0 END AS nullable,
                c.CHARACTER_MAXIMUM_LENGTH AS max_len,
                c.NUMERIC_PRECISION AS prec, c.NUMERIC_SCALE AS scale, c.COLUMN_DEFAULT AS dflt,
                COLUMNPROPERTY(OBJECT_ID(QUOTENAME(c.TABLE_SCHEMA) + '.' + QUOTENAME(c.TABLE_NAME)), c.COLUMN_NAME, 'IsIdentity') AS is_id,
                COLUMNPROPERTY(OBJECT_ID(QUOTENAME(c.TABLE_SCHEMA) + '.' + QUOTENAME(c.TABLE_NAME)), c.COLUMN_NAME, 'IsComputed') AS is_comp
         FROM INFORMATION_SCHEMA.COLUMNS c
         ORDER BY c.TABLE_SCHEMA, c.TABLE_NAME, c.ORDINAL_POSITION`,
      );
      const columnsByTable = new Map<string, ColumnModel[]>();
      for (const r of extractRawRows(colResult, "mssql")) {
        const key = `${r.schema_name}:${r.tbl}`;
        if (!includedKeys.has(key)) continue;
        const { edmType, isFallback } = SqlServerIntrospector.mapType(r.dt as string);
        if (!columnsByTable.has(key)) columnsByTable.set(key, []);
        const maxLen = r.max_len as number | null;
        const ssCol: ColumnModel = {
          dbName: r.col as string,
          exposedName: r.col as string,
          dbType: r.dt as string,
          edmType,
          isFallbackType: isFallback,
          nullable: (r.nullable as number) === 1,
          isAutoGenerated: r.is_id != null && Number(r.is_id) === 1,
          isComputed: r.is_comp != null && Number(r.is_comp) === 1,
          isPrimaryKey: false,
        };
        if (maxLen !== null && maxLen !== -1) (ssCol as unknown as Record<string, unknown>)["maxLength"] = maxLen;
        if (r.prec != null) (ssCol as unknown as Record<string, unknown>)["precision"] = Number(r.prec);
        if (r.scale != null) (ssCol as unknown as Record<string, unknown>)["scale"] = Number(r.scale);
        const dflt = r.dflt as string | null;
        if (dflt !== null) (ssCol as unknown as Record<string, unknown>)["defaultExpression"] = dflt;
        columnsByTable.get(key)!.push(ssCol);
      }

      const keyResult = await db.raw(
        `SELECT tc.TABLE_SCHEMA AS schema_name, tc.TABLE_NAME AS tbl, tc.CONSTRAINT_NAME AS con,
                CASE WHEN tc.CONSTRAINT_TYPE = 'PRIMARY KEY' THEN 1 ELSE 0 END AS is_pk,
                kcu.COLUMN_NAME AS col, kcu.ORDINAL_POSITION AS ord
         FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
         JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE kcu
           ON kcu.CONSTRAINT_NAME = tc.CONSTRAINT_NAME AND kcu.CONSTRAINT_SCHEMA = tc.CONSTRAINT_SCHEMA
         WHERE tc.CONSTRAINT_TYPE IN ('PRIMARY KEY', 'UNIQUE')
         ORDER BY tc.TABLE_SCHEMA, tc.TABLE_NAME, tc.CONSTRAINT_NAME, kcu.ORDINAL_POSITION`,
      );
      const keysByTable = new Map<string, Array<{ con: string; isPrimary: boolean; col: string; ord: number }>>();
      for (const r of extractRawRows(keyResult, "mssql")) {
        const key = `${r.schema_name}:${r.tbl}`;
        if (!includedKeys.has(key)) continue;
        if (!keysByTable.has(key)) keysByTable.set(key, []);
        keysByTable.get(key)!.push({ con: r.con as string, isPrimary: (r.is_pk as number) === 1, col: r.col as string, ord: r.ord as number });
      }

      const fkResult = await db.raw(
        `SELECT fk.name AS fk_name, ps.name AS schema_name, pt.name AS tbl, pc.name AS col,
                rs.name AS ref_schema, rt.name AS ref_tbl, rc.name AS ref_col,
                fkc.constraint_column_id AS ord
         FROM sys.foreign_keys fk
         JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
         JOIN sys.tables pt ON pt.object_id = fk.parent_object_id
         JOIN sys.schemas ps ON ps.schema_id = pt.schema_id
         JOIN sys.columns pc ON pc.object_id = fkc.parent_object_id AND pc.column_id = fkc.parent_column_id
         JOIN sys.tables rt ON rt.object_id = fk.referenced_object_id
         JOIN sys.schemas rs ON rs.schema_id = rt.schema_id
         JOIN sys.columns rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id
         ORDER BY fk.name, fkc.constraint_column_id`,
      );
      const fksRaw = extractRawRows(fkResult, "mssql").map((r) => ({
        name: r.fk_name as string,
        schema: r.schema_name as string,
        tbl: r.tbl as string,
        col: r.col as string,
        refSchema: r.ref_schema as string,
        refTbl: r.ref_tbl as string,
        refCol: r.ref_col as string,
        ord: r.ord as number,
      }));

      const tables: TableModel[] = [];
      for (const { schema, name, isView, comment } of included) {
        const key = `${schema}:${name}`;
        const keys = keysByTable.get(key) ?? [];
        const pkCols = keys.filter((k) => k.isPrimary).sort((a, b) => a.ord - b.ord).map((k) => k.col);
        const pkSet = new Set(pkCols);
        const columns = (columnsByTable.get(key) ?? []).map((c) =>
          pkSet.has(c.exposedName) ? { ...c, isPrimaryKey: true, nullable: false } : c,
        );
        const uniqueGroups = groupBy(keys.filter((k) => !k.isPrimary), (k) => k.con);
        const uniques = Object.values(uniqueGroups).map((g) =>
          g.sort((a, b) => a.ord - b.ord).map((k) => k.col),
        );

        const ssTable: TableModel = {
          dbSchema: schema,
          dbName: name,
          exposedName: schema === "dbo" ? name : `${schema}_${name}`,
          isView,
          writable: !isView,
          columns,
          primaryKey: pkCols,
          uniqueConstraints: uniques,
          foreignKeys: [],
        };
        if (comment != null) (ssTable as unknown as Record<string, unknown>)["comment"] = comment;
        tables.push(ssTable);
      }

      for (let i = 0; i < tables.length; i++) {
        const t = tables[i]!;
        const childTaken = new Set(t.columns.map((c) => c.exposedName));
        const fks: ForeignKeyModel[] = [];
        const grouped = groupBy(fksRaw.filter((f) => f.schema === t.dbSchema && f.tbl === t.dbName), (f) => f.name);
        for (const [fkName, group] of Object.entries(grouped).sort(([a], [b]) => a.localeCompare(b))) {
          const first = group[0]!;
          const parent = tables.find((p) => p.dbSchema === first.refSchema && p.dbName === first.refTbl);
          if (!parent) continue;
          const parentTaken = new Set([...parent.columns.map((c) => c.exposedName), ...parent.foreignKeys.map((f) => f.navToMany)]);
          const ordered = [...group].sort((a, b) => a.ord - b.ord);
          const fromColumns = ordered.map((g) => g.col);
          fks.push({
            name: fkName,
            columns: fromColumns,
            refTable: parent.exposedName,
            refColumns: ordered.map((g) => g.refCol),
            navToOne: toOneName(fromColumns, parent.exposedName, fkName, childTaken),
            navToMany: toManyName(t.exposedName, fkName, parentTaken),
          });
        }
        tables[i] = { ...t, foreignKeys: fks };
      }

      return {
        version: 1,
        engine: "sqlserver",
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
