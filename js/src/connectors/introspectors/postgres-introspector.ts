import type { SchemaIntrospector, ConnectionSpec, IntrospectionOptions } from "../contracts.js";
import type { ColumnModel, ForeignKeyModel, SchemaSnapshot, TableModel } from "../../core/schema.js";
import { createKnex, extractRawRows } from "../knex-helpers.js";
import { toOneName, toManyName } from "../navigation-naming.js";

/** PostgreSQL udt_name → EDM type (port of PostgreSqlTypeMap.cs, spec 04 §6). */
function mapPgType(udtName: string): { edmType: string; isFallback: boolean } {
  if (udtName.startsWith("_")) {
    const { edmType: element, isFallback: fallback } = mapPgType(udtName.slice(1));
    return fallback
      ? { edmType: "Edm.String", isFallback: true }
      : { edmType: `Collection(${element})`, isFallback: false };
  }
  switch (udtName) {
    case "int2":
      return { edmType: "Edm.Int16", isFallback: false };
    case "int4":
      return { edmType: "Edm.Int32", isFallback: false };
    case "int8":
      return { edmType: "Edm.Int64", isFallback: false };
    case "numeric":
    case "money":
      return { edmType: "Edm.Decimal", isFallback: false };
    case "float4":
      return { edmType: "Edm.Single", isFallback: false };
    case "float8":
      return { edmType: "Edm.Double", isFallback: false };
    case "bool":
      return { edmType: "Edm.Boolean", isFallback: false };
    case "text":
    case "varchar":
    case "bpchar":
    case "citext":
    case "name":
      return { edmType: "Edm.String", isFallback: false };
    case "uuid":
      return { edmType: "Edm.Guid", isFallback: false };
    case "timestamptz":
    case "timestamp":
      return { edmType: "Edm.DateTimeOffset", isFallback: false };
    case "date":
      return { edmType: "Edm.Date", isFallback: false };
    case "time":
    case "timetz":
      return { edmType: "Edm.TimeOfDay", isFallback: false };
    case "interval":
      return { edmType: "Edm.Duration", isFallback: false };
    case "bytea":
      return { edmType: "Edm.Binary", isFallback: false };
    case "json":
    case "jsonb":
      return { edmType: "Edm.Untyped", isFallback: false };
    default:
      return { edmType: "Edm.String", isFallback: true }; // CON-8: explicit fallback
  }
}

function exposeName(dbName: string, style: string): string {
  if (style !== "pascal") return dbName;
  return dbName
    .split("_")
    .filter((p) => p.length > 0)
    .map((p) => p[0]!.toUpperCase() + p.slice(1))
    .join("");
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

function matchGlob(value: string, pattern: string): boolean {
  const re = new RegExp("^" + pattern.replace(/\*/g, ".*").replace(/\?/g, ".") + "$", "i");
  return re.test(value);
}

/**
 * Schema discovery via information_schema + pg_catalog (port of PostgreSqlIntrospector.cs, spec 04 §4).
 * FK column resolution uses pg_constraint/pg_attribute for reliable composite key ordering.
 */
export class PostgreSqlIntrospector implements SchemaIntrospector {
  async introspect(
    spec: ConnectionSpec,
    options: IntrospectionOptions,
    _signal?: AbortSignal,
  ): Promise<SchemaSnapshot> {
    const db = createKnex(spec, "pg");
    try {
      const rawTables = await this.loadTables(db);
      const rawColumns = await this.loadColumns(db);
      const keyConstraints = await this.loadKeyConstraints(db);
      const foreignKeys = await this.loadForeignKeys(db);

      const included = rawTables
        .filter(
          (t) =>
            options.includeSchemas.length === 0 ||
            options.includeSchemas.some((p) => matchGlob(t.schema, p)),
        )
        .filter(
          (t) =>
            options.excludeTables.length === 0 ||
            !options.excludeTables.some((p) => matchGlob(t.name, p)),
        )
        .filter((t) => options.includeViews || !t.isView);

      const includedKeys = new Set(included.map((t) => `${t.schema}:${t.name}`));

      const tables: TableModel[] = [];
      for (const raw of included) {
        const columns = rawColumns
          .filter((c) => c.schema === raw.schema && c.table === raw.name)
          .sort((a, b) => a.ordinal - b.ordinal);

        const primaryKey = keyConstraints
          .filter((k) => k.schema === raw.schema && k.table === raw.name && k.isPrimary)
          .sort((a, b) => a.ordinal - b.ordinal)
          .map((k) => exposeName(k.column, options.exposedNameStyle));

        const uniqueGroups = groupBy(
          keyConstraints.filter(
            (k) => k.schema === raw.schema && k.table === raw.name && !k.isPrimary,
          ),
          (k) => k.constraintName,
        );
        const uniques = Object.values(uniqueGroups).map((g) =>
          g.sort((a, b) => a.ordinal - b.ordinal).map((k) => exposeName(k.column, options.exposedNameStyle)),
        );

        const pkSet = new Set(primaryKey);
        const columnModels: ColumnModel[] = columns.map((c) => {
          const { edmType, isFallback } = mapPgType(c.udtName);
          const exposed = exposeName(c.name, options.exposedNameStyle);
          const base: ColumnModel = {
            dbName: c.name,
            exposedName: exposed,
            dbType: c.udtName,
            edmType,
            isFallbackType: isFallback,
            nullable: c.nullable && !pkSet.has(exposed),
            isPrimaryKey: pkSet.has(exposed),
            isAutoGenerated:
              c.isIdentity || (c.defaultValue?.startsWith("nextval(") ?? false),
            isComputed: c.isGenerated,
          };
          if (c.maxLength !== null) (base as unknown as Record<string, unknown>)["maxLength"] = c.maxLength;
          if (c.precision !== null) (base as unknown as Record<string, unknown>)["precision"] = c.precision;
          if (c.scale !== null) (base as unknown as Record<string, unknown>)["scale"] = c.scale;
          if (c.defaultValue !== null) (base as unknown as Record<string, unknown>)["defaultExpression"] = c.defaultValue;
          if (c.comment !== null) (base as unknown as Record<string, unknown>)["comment"] = c.comment;
          return base;
        });

        const tbl: TableModel = {
          dbSchema: raw.schema,
          dbName: raw.name,
          exposedName: exposeName(raw.name, options.exposedNameStyle),
          isView: raw.isView,
          writable: !raw.isView,
          columns: columnModels,
          primaryKey,
          uniqueConstraints: uniques,
          foreignKeys: [],
        };
        if (raw.comment !== null) (tbl as unknown as Record<string, unknown>)["comment"] = raw.comment;
        tables.push(tbl);
      }

      this.attachForeignKeys(tables, foreignKeys, includedKeys, options);

      return {
        version: 1,
        engine: "postgresql",
        collectedAt: new Date().toISOString(),
        tables: [...tables].sort((a, b) => a.exposedName.localeCompare(b.exposedName)),
      };
    } finally {
      await db.destroy();
    }
  }

  private attachForeignKeys(
    tables: TableModel[],
    foreignKeys: RawFk[],
    includedKeys: Set<string>,
    options: IntrospectionOptions,
  ): void {
    const childTaken = new Map(
      tables.map((t) => [
        `${t.dbSchema}:${t.dbName}`,
        new Set(t.columns.map((c) => c.exposedName)),
      ]),
    );
    const parentTaken = new Map(
      tables.map((t) => [
        `${t.dbSchema}:${t.dbName}`,
        new Set(t.columns.map((c) => c.exposedName)),
      ]),
    );
    const fksByTable = new Map<string, ForeignKeyModel[]>(
      tables.map((t) => [`${t.dbSchema}:${t.dbName}`, []]),
    );

    const sortedFks = [...foreignKeys].sort((a, b) => a.name.localeCompare(b.name));
    for (const fk of sortedFks) {
      if (
        !includedKeys.has(`${fk.schema}:${fk.table}`) ||
        !includedKeys.has(`${fk.refSchema}:${fk.refTable}`)
      ) {
        continue;
      }
      const child = tables.find((t) => t.dbSchema === fk.schema && t.dbName === fk.table)!;
      const parent = tables.find((t) => t.dbSchema === fk.refSchema && t.dbName === fk.refTable)!;

      const ck = `${child.dbSchema}:${child.dbName}`;
      const pk = `${parent.dbSchema}:${parent.dbName}`;

      const navToOne = toOneName(
        fk.columns.map((c) => exposeName(c, options.exposedNameStyle)),
        parent.exposedName,
        fk.name,
        childTaken.get(ck)!,
      );
      const navToMany = toManyName(child.exposedName, fk.name, parentTaken.get(pk)!);

      fksByTable.get(ck)!.push({
        name: fk.name,
        columns: fk.columns.map((c) => exposeName(c, options.exposedNameStyle)),
        refTable: parent.exposedName,
        refColumns: fk.refColumns.map((c) => exposeName(c, options.exposedNameStyle)),
        navToOne,
        navToMany,
      });
    }

    for (let i = 0; i < tables.length; i++) {
      const t = tables[i]!;
      const key = `${t.dbSchema}:${t.dbName}`;
      tables[i] = { ...t, foreignKeys: fksByTable.get(key) ?? [] };
    }
  }

  private async loadTables(db: ReturnType<typeof createKnex>): Promise<RawTable[]> {
    const sql = `
      SELECT n.nspname AS schema, c.relname AS name, c.relkind AS kind,
             obj_description(c.oid, 'pg_class') AS comment
      FROM pg_class c
      JOIN pg_namespace n ON n.oid = c.relnamespace
      WHERE c.relkind IN ('r', 'p', 'v', 'm')
        AND n.nspname NOT IN ('pg_catalog', 'information_schema')
        AND n.nspname NOT LIKE 'pg_%'
      ORDER BY n.nspname, c.relname`;
    const result = await db.raw(sql);
    return extractRawRows(result, "pg").map((r) => ({
      schema: r["schema"] as string,
      name: r["name"] as string,
      isView: (r["kind"] as string) === "v" || (r["kind"] as string) === "m",
      comment: r["comment"] as string | null,
    }));
  }

  private async loadColumns(db: ReturnType<typeof createKnex>): Promise<RawColumn[]> {
    const sql = `
      SELECT c.table_schema AS schema, c.table_name AS tbl, c.column_name AS col,
             c.ordinal_position AS ord, c.udt_name AS udt,
             CASE WHEN c.is_nullable = 'YES' THEN true ELSE false END AS nullable,
             c.character_maximum_length AS max_len,
             c.numeric_precision AS prec, c.numeric_scale AS scale,
             c.column_default AS dflt,
             CASE WHEN c.is_identity = 'YES' THEN true ELSE false END AS is_identity,
             CASE WHEN c.is_generated = 'ALWAYS' THEN true ELSE false END AS is_generated,
             col_description(
               format('%I.%I', c.table_schema, c.table_name)::regclass::oid,
               c.ordinal_position
             ) AS comment
      FROM information_schema.columns c
      WHERE c.table_schema NOT IN ('pg_catalog', 'information_schema')
      ORDER BY c.table_schema, c.table_name, c.ordinal_position`;
    const result = await db.raw(sql);
    return extractRawRows(result, "pg").map((r) => ({
      schema: r["schema"] as string,
      table: r["tbl"] as string,
      name: r["col"] as string,
      ordinal: r["ord"] as number,
      udtName: r["udt"] as string,
      nullable: r["nullable"] as boolean,
      maxLength: r["max_len"] as number | null,
      precision: r["prec"] as number | null,
      scale: r["scale"] as number | null,
      defaultValue: r["dflt"] as string | null,
      isIdentity: r["is_identity"] as boolean,
      isGenerated: r["is_generated"] as boolean,
      comment: r["comment"] as string | null,
    }));
  }

  private async loadKeyConstraints(db: ReturnType<typeof createKnex>): Promise<RawKey[]> {
    const sql = `
      SELECT tc.table_schema AS schema, tc.table_name AS tbl, tc.constraint_name AS con,
             CASE WHEN tc.constraint_type = 'PRIMARY KEY' THEN true ELSE false END AS is_primary,
             kcu.column_name AS col, kcu.ordinal_position AS ord
      FROM information_schema.table_constraints tc
      JOIN information_schema.key_column_usage kcu
        ON kcu.constraint_name = tc.constraint_name
       AND kcu.constraint_schema = tc.constraint_schema
      WHERE tc.constraint_type IN ('PRIMARY KEY', 'UNIQUE')
        AND tc.table_schema NOT IN ('pg_catalog', 'information_schema')
      ORDER BY tc.table_schema, tc.table_name, tc.constraint_name, kcu.ordinal_position`;
    const result = await db.raw(sql);
    return extractRawRows(result, "pg").map((r) => ({
      schema: r["schema"] as string,
      table: r["tbl"] as string,
      constraintName: r["con"] as string,
      isPrimary: r["is_primary"] as boolean,
      column: r["col"] as string,
      ordinal: r["ord"] as number,
    }));
  }

  private async loadForeignKeys(db: ReturnType<typeof createKnex>): Promise<RawFk[]> {
    const sql = `
      SELECT con.conname AS fk_name,
             srcns.nspname AS schema, src.relname AS tbl,
             tgtns.nspname AS ref_schema, tgt.relname AS ref_tbl,
             (SELECT json_agg(a.attname ORDER BY k.ord)
                FROM unnest(con.conkey) WITH ORDINALITY AS k(attnum, ord)
                JOIN pg_attribute a ON a.attrelid = con.conrelid AND a.attnum = k.attnum) AS cols,
             (SELECT json_agg(a.attname ORDER BY k.ord)
                FROM unnest(con.confkey) WITH ORDINALITY AS k(attnum, ord)
                JOIN pg_attribute a ON a.attrelid = con.confrelid AND a.attnum = k.attnum) AS ref_cols
      FROM pg_constraint con
      JOIN pg_class src ON src.oid = con.conrelid
      JOIN pg_namespace srcns ON srcns.oid = src.relnamespace
      JOIN pg_class tgt ON tgt.oid = con.confrelid
      JOIN pg_namespace tgtns ON tgtns.oid = tgt.relnamespace
      WHERE con.contype = 'f'
        AND srcns.nspname NOT IN ('pg_catalog', 'information_schema')
      ORDER BY con.conname`;
    const result = await db.raw(sql);
    return extractRawRows(result, "pg").map((r) => ({
      name: r["fk_name"] as string,
      schema: r["schema"] as string,
      table: r["tbl"] as string,
      refSchema: r["ref_schema"] as string,
      refTable: r["ref_tbl"] as string,
      columns: r["cols"] as string[],
      refColumns: r["ref_cols"] as string[],
    }));
  }
}

interface RawTable {
  schema: string;
  name: string;
  isView: boolean;
  comment: string | null;
}

interface RawColumn {
  schema: string;
  table: string;
  name: string;
  ordinal: number;
  udtName: string;
  nullable: boolean;
  maxLength: number | null;
  precision: number | null;
  scale: number | null;
  defaultValue: string | null;
  isIdentity: boolean;
  isGenerated: boolean;
  comment: string | null;
}

interface RawKey {
  schema: string;
  table: string;
  constraintName: string;
  isPrimary: boolean;
  column: string;
  ordinal: number;
}

interface RawFk {
  name: string;
  schema: string;
  table: string;
  refSchema: string;
  refTable: string;
  columns: string[];
  refColumns: string[];
}
