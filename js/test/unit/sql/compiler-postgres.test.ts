import { describe, expect, it } from "vitest";
import { QueryValidationError } from "../../../src/connectors/contracts.js";
import { PostgreSqlDialect } from "../../../src/connectors/dialects/postgres.js";
import { SqlCompiler } from "../../../src/connectors/sql/compiler.js";
import {
  comparison,
  constant,
  constantArg,
  fieldArg,
  fieldRef,
  fn,
  inList,
  lambda,
  logical,
  NULL_CONSTANT,
} from "../../../src/core/query.js";
import { col, query, snapshot, table } from "../../helpers/schema.js";

/**
 * Dialect translation tests for PostgreSQL (spec 04 §7.2) plus the SQL safety
 * invariants of spec 13 §4 (port of SqlCompilerPostgreSqlTests.cs): client-supplied
 * values must never appear in SQL text.
 */
describe("SqlCompiler + PostgreSqlDialect", () => {
  const TAINT = "EZTAINT_7f3a"; // any appearance in SQL text = injection bug

  const schema = snapshot("postgresql", [
    table({
      dbSchema: "public",
      dbName: "customers",
      primaryKey: ["id"],
      columns: [
        col("id", "Edm.Int32", { isPrimaryKey: true }),
        col("name", "Edm.String"),
        col("country", "Edm.String"),
        col("created_at", "Edm.DateTimeOffset"),
      ],
    }),
    table({
      dbSchema: "public",
      dbName: "orders",
      primaryKey: ["id"],
      columns: [
        col("id", "Edm.Int64", { isPrimaryKey: true }),
        col("customer_id", "Edm.Int32"),
        col("total", "Edm.Decimal"),
        col("status", "Edm.String"),
      ],
      foreignKeys: [
        {
          name: "fk_orders_customer",
          columns: ["customer_id"],
          refTable: "customers",
          refColumns: ["id"],
          navToOne: "customer",
          navToMany: "orders",
        },
      ],
    }),
  ]);

  const compiler = new SqlCompiler(new PostgreSqlDialect());
  const q = (t = "customers", overrides = {}) => query("svc", t, overrides);

  it("projects all columns with a pk tiebreaker", () => {
    const compiled = compiler.compileSelect(schema, q("customers", { top: 25 }));
    expect(compiled.sql).toBe(
      'SELECT t0."id" AS "id", t0."name" AS "name", t0."country" AS "country", t0."created_at" AS "created_at"' +
        ' FROM "public"."customers" AS t0 ORDER BY t0."id" LIMIT 26',
    );
    expect(compiled.parameters).toHaveLength(0);
  });

  it("limits columns by projection", () => {
    const compiled = compiler.compileSelect(schema, q("customers", { select: ["id", "name"] }));
    expect(compiled.sql.startsWith('SELECT t0."id" AS "id", t0."name" AS "name" FROM')).toBe(true);
  });

  it("throws unknown property for an unknown $select field", () => {
    try {
      compiler.compileSelect(schema, q("customers", { select: ["nope"] }));
      expect.unreachable();
    } catch (err) {
      expect(err).toBeInstanceOf(QueryValidationError);
      expect((err as QueryValidationError).code).toBe("Validation.UnknownProperty");
    }
  });

  it("parameterizes comparison values", () => {
    const compiled = compiler.compileSelect(schema, q("customers", { filter: comparison(fieldRef("name"), "eq", constant(TAINT)) }));
    expect(compiled.sql).toContain('WHERE t0."name" = @p0');
    expect(compiled.sql).not.toContain(TAINT);
    expect(compiled.parameters).toHaveLength(1);
    expect(compiled.parameters[0]!.value).toBe(TAINT);
  });

  it("renders IS NULL / IS NOT NULL", () => {
    expect(compiler.compileSelect(schema, q("customers", { filter: comparison(fieldRef("country"), "eq", NULL_CONSTANT) })).sql).toContain(
      't0."country" IS NULL',
    );
    expect(compiler.compileSelect(schema, q("customers", { filter: comparison(fieldRef("country"), "ne", NULL_CONSTANT) })).sql).toContain(
      't0."country" IS NOT NULL',
    );
  });

  it("renders logical composition parenthesized", () => {
    const compiled = compiler.compileSelect(
      schema,
      q("orders", {
        filter: logical("and", [comparison(fieldRef("status"), "eq", constant("open")), comparison(fieldRef("total"), "gt", constant(250.0))]),
      }),
    );
    expect(compiled.sql).toContain('WHERE (t0."status" = @p0 AND t0."total" > @p1)');
    expect(compiled.parameters).toHaveLength(2);
  });

  it("compiles contains to ILIKE with escaped pattern", () => {
    const compiled = compiler.compileSelect(
      schema,
      q("customers", { filter: fn("contains", [fieldArg(fieldRef("name")), constantArg(constant("50%_off"))]) }),
    );
    expect(compiled.sql).toContain('t0."name" ILIKE @p0 ESCAPE \'\\\'');
    expect(compiled.parameters[0]!.value).toBe("%50\\%\\_off%");
  });

  it("anchors startswith and endswith patterns", () => {
    const starts = compiler.compileSelect(schema, q("customers", { filter: fn("startsWith", [fieldArg(fieldRef("name")), constantArg(constant("Ac"))]) }));
    expect(starts.parameters[0]!.value).toBe("Ac%");

    const ends = compiler.compileSelect(schema, q("customers", { filter: fn("endsWith", [fieldArg(fieldRef("name")), constantArg(constant("Corp"))]) }));
    expect(ends.parameters[0]!.value).toBe("%Corp");
  });

  it("renders EXTRACT for the year function comparison", () => {
    const compiled = compiler.compileSelect(schema, q("customers", { filter: fn("year", [fieldArg(fieldRef("created_at"))], "eq", constant(2026)) }));
    expect(compiled.sql).toContain('EXTRACT(YEAR FROM t0."created_at")::int = @p0');
    expect(compiled.parameters[0]!.value).toBe(2026);
  });

  it("parameterizes every value in an IN list", () => {
    const compiled = compiler.compileSelect(
      schema,
      q("orders", { filter: inList(fieldRef("status"), [constant("open"), constant("shipped"), constant(TAINT)]) }),
    );
    expect(compiled.sql).toContain('t0."status" IN (@p0, @p1, @p2)');
    expect(compiled.sql).not.toContain(TAINT);
    expect(compiled.parameters).toHaveLength(3);
  });

  it("compiles a to-one navigation filter to a LEFT JOIN", () => {
    const compiled = compiler.compileSelect(schema, q("orders", { filter: comparison(fieldRef("customer", "country"), "eq", constant("DE")) }));
    expect(compiled.sql).toContain('LEFT JOIN "public"."customers" AS t1 ON t0."customer_id" = t1."id"');
    expect(compiled.sql).toContain('WHERE t1."country" = @p0');
  });

  it("compiles an any() lambda to EXISTS", () => {
    const compiled = compiler.compileSelect(
      schema,
      q("customers", { filter: lambda("orders", "any", comparison(fieldRef("total"), "gt", constant(100))) }),
    );
    expect(compiled.sql).toContain(
      'EXISTS (SELECT 1 FROM "public"."orders" AS l0 WHERE l0."customer_id" = t0."id" AND l0."total" > @p0)',
    );
  });

  it("compiles an all() lambda to a double negation", () => {
    const compiled = compiler.compileSelect(
      schema,
      q("customers", { filter: lambda("orders", "all", comparison(fieldRef("status"), "eq", constant("shipped"))) }),
    );
    expect(compiled.sql).toContain('NOT EXISTS (SELECT 1 FROM');
    expect(compiled.sql).toContain('AND NOT (l0."status" = @p0)');
  });

  it("appends the pk tiebreaker to $orderby exactly once", () => {
    const desc = compiler.compileSelect(schema, q("customers", { orderBy: [{ field: "name", descending: true }] }));
    expect(desc.sql).toContain('ORDER BY t0."name" DESC, t0."id"');

    const byPk = compiler.compileSelect(schema, q("customers", { orderBy: [{ field: "id", descending: false }] }));
    expect(byPk.sql).toContain('ORDER BY t0."id"');
    expect(byPk.sql).not.toContain('t0."id", t0."id"');
  });

  it("renders OFFSET for skip", () => {
    const compiled = compiler.compileSelect(schema, q("customers", { top: 10, skip: 30 }));
    expect(compiled.sql.endsWith("LIMIT 11 OFFSET 30")).toBe(true);
  });

  it("uses the same WHERE for count without pagination", () => {
    const compiled = compiler.compileCount(schema, q("orders", { filter: comparison(fieldRef("status"), "eq", constant("open")), top: 5, skip: 10 }));
    expect(compiled.sql).toBe('SELECT COUNT(*) FROM "public"."orders" AS t0 WHERE t0."status" = @p0');
  });

  it("throws on an unknown table", () => {
    expect(() => compiler.compileSelect(schema, q("ghosts"))).toThrow(QueryValidationError);
  });

  it("keeps SQL injection attempts inside parameters", () => {
    const payloads = ["'; DROP TABLE customers; --", "1 OR 1=1", '" OR ""="', "'; EXEC xp_cmdshell('dir'); --"];
    for (const payload of payloads) {
      const compiled = compiler.compileSelect(schema, q("customers", { filter: comparison(fieldRef("name"), "eq", constant(payload)) }));
      expect(compiled.sql).not.toContain("DROP");
      expect(compiled.sql).not.toContain("xp_cmdshell");
      expect(compiled.sql).not.toContain(payload);
      expect(compiled.parameters).toHaveLength(1);
      expect(compiled.parameters[0]!.value).toBe(payload);
    }
  });
});
