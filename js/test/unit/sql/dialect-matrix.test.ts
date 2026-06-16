import { describe, expect, it } from "vitest";
import { MySqlDialect } from "../../../src/connectors/dialects/mysql.js";
import { SqlServerDialect } from "../../../src/connectors/dialects/sqlserver.js";
import { SqliteDialect } from "../../../src/connectors/dialects/sqlite.js";
import { SqlCompiler } from "../../../src/connectors/sql/compiler.js";
import { WriteSqlCompiler } from "../../../src/connectors/sql/write-compiler.js";
import { MySqlIntrospector } from "../../../src/connectors/introspectors/mysql-introspector.js";
import { SqlServerIntrospector } from "../../../src/connectors/introspectors/sqlserver-introspector.js";
import { SqliteIntrospector } from "../../../src/connectors/introspectors/sqlite-introspector.js";
import { comparison, constant, fieldRef } from "../../../src/core/query.js";
import { col, query, snapshot, table } from "../../helpers/schema.js";

/**
 * Dialect translation matrix (port of DialectMatrixTests.cs, spec 04 §7.2):
 * each non-PostgreSQL engine's quoting, pagination, functions, and type mapping.
 */
describe("Dialect matrix", () => {
  const schema = snapshot("any", [
    table({
      dbName: "orders",
      primaryKey: ["id"],
      columns: [
        col("id", "Edm.Int64", { isPrimaryKey: true }),
        col("status", "Edm.String"),
        col("total", "Edm.Decimal"),
        col("created_at", "Edm.DateTimeOffset"),
      ],
    }),
  ]);
  const ordersTable = schema.tables[0]!;
  const q = (overrides = {}) => query("s", "orders", overrides);

  // ---- MySQL ----

  describe("MySQL", () => {
    const dialect = new MySqlDialect();
    const compiler = new SqlCompiler(dialect);
    const writeCompiler = new WriteSqlCompiler(dialect);

    it("uses backtick quoting and LIMIT/OFFSET pagination", () => {
      const c = compiler.compileSelect(schema, q({ top: 10, skip: 5 }));
      expect(c.sql).toContain("`orders`");
      expect(c.sql.endsWith("LIMIT 11 OFFSET 5")).toBe(true);
    });

    it("requires LIMIT before OFFSET when only skip is given", () => {
      const c = compiler.compileSelect(schema, q({ skip: 5 }));
      expect(c.sql).toContain("LIMIT 18446744073709551615 OFFSET 5");
    });

    it("maps length to CHAR_LENGTH", () => {
      expect(dialect.mapFunction("length", ["`c`"])).toBe("CHAR_LENGTH(`c`)");
    });

    it("maps indexOf to LOCATE with inverted arg order", () => {
      expect(dialect.mapFunction("indexOf", ["`c`", "@p0"])).toBe("(LOCATE(@p0, `c`) - 1)");
    });

    it("maps year to YEAR()", () => {
      expect(dialect.mapFunction("year", ["`c`"])).toBe("YEAR(`c`)");
    });

    it("maps concat to CONCAT()", () => {
      expect(dialect.mapFunction("concat", ["`a`", "`b`"])).toBe("CONCAT(`a`, `b`)");
    });

    it("maps contains to LIKE with escape", () => {
      expect(dialect.mapFunction("contains", ["`c`", "@p0"])).toContain("LIKE");
    });

    it("returning mode is none (uses LAST_INSERT_ID strategy)", () => {
      expect(dialect.returning).toBe("none");
    });

    it("INSERT has no RETURNING clause", () => {
      const c = writeCompiler.compileInsert(
        schema,
        ordersTable,
        { values: { status: "open" }, children: {} },
        false,
      );
      expect(c.sql).toBe("INSERT INTO `orders` (`status`) VALUES (@p0)");
    });

    it("injection payload stays in parameter", () => {
      const payload = "'; DROP TABLE orders; --";
      const c = compiler.compileSelect(
        schema,
        q({ filter: comparison(fieldRef("status"), "eq", constant(payload)) }),
      );
      expect(c.sql).not.toContain("DROP");
      expect(c.parameters[0]!.value).toBe(payload);
    });
  });

  // ---- SQL Server ----

  describe("SQL Server", () => {
    const dialect = new SqlServerDialect();
    const compiler = new SqlCompiler(dialect);
    const writeCompiler = new WriteSqlCompiler(dialect);

    it("uses bracket quoting and OFFSET..FETCH pagination", () => {
      const c = compiler.compileSelect(schema, q({ top: 10, skip: 5 }));
      expect(c.sql).toContain("[orders]");
      expect(c.sql).toContain("ORDER BY"); // OFFSET..FETCH requires ORDER BY
      expect(c.sql.endsWith("OFFSET 5 ROWS FETCH NEXT 11 ROWS ONLY")).toBe(true);
    });

    it("top without skip still uses OFFSET 0 ROWS FETCH", () => {
      const c = compiler.compileSelect(schema, q({ top: 3 }));
      expect(c.sql.endsWith("OFFSET 0 ROWS FETCH NEXT 4 ROWS ONLY")).toBe(true);
    });

    it("maps length to LEN()", () => {
      expect(dialect.mapFunction("length", ["[c]"])).toBe("LEN([c])");
    });

    it("maps indexOf to CHARINDEX with inverted args", () => {
      expect(dialect.mapFunction("indexOf", ["[c]", "@p0"])).toBe("(CHARINDEX(@p0, [c]) - 1)");
    });

    it("maps year to DATEPART(year, ...)", () => {
      expect(dialect.mapFunction("year", ["[c]"])).toBe("DATEPART(year, [c])");
    });

    it("maps ceiling to CEILING()", () => {
      expect(dialect.mapFunction("ceiling", ["[c]"])).toBe("CEILING([c])");
    });

    it("returning mode is outputClause", () => {
      expect(dialect.returning).toBe("outputClause");
    });

    it("INSERT uses OUTPUT INSERTED clause", () => {
      const c = writeCompiler.compileInsert(
        schema,
        ordersTable,
        { values: { status: "open" }, children: {} },
        true,
      );
      expect(c.sql).toContain("OUTPUT INSERTED.[id] AS [id]");
      expect(c.sql).toContain(") OUTPUT");
      expect(c.sql).toContain("VALUES (@p0)");
      expect(c.sql).not.toContain("RETURNING");
    });

    it("injection payload stays in parameter", () => {
      const payload = "'; DROP TABLE orders; --";
      const c = compiler.compileSelect(
        schema,
        q({ filter: comparison(fieldRef("status"), "eq", constant(payload)) }),
      );
      expect(c.sql).not.toContain("DROP");
      expect(c.parameters[0]!.value).toBe(payload);
    });
  });

  // ---- SQLite ----

  describe("SQLite", () => {
    const dialect = new SqliteDialect();
    const compiler = new SqlCompiler(dialect);
    const writeCompiler = new WriteSqlCompiler(dialect);

    it("uses double-quote quoting and LIMIT -1 OFFSET for skip-only", () => {
      const c = compiler.compileSelect(schema, q({ skip: 5 }));
      expect(c.sql).toContain('"orders"');
      expect(c.sql.endsWith("LIMIT -1 OFFSET 5")).toBe(true);
    });

    it("maps year to CAST(strftime(...))", () => {
      expect(dialect.mapFunction("year", ['"c"'])).toBe("CAST(strftime('%Y', \"c\") AS INTEGER)");
    });

    it("maps indexOf to instr() with natural arg order", () => {
      expect(dialect.mapFunction("indexOf", ['"c"', "@p0"])).toBe('(instr("c", @p0) - 1)');
    });

    it("maps floor with CASE WHEN emulation", () => {
      expect(dialect.mapFunction("floor", ['"c"'])).toContain("CASE WHEN");
    });

    it("returning mode is returningSuffix", () => {
      expect(dialect.returning).toBe("returningSuffix");
    });

    it("INSERT appends RETURNING", () => {
      const c = writeCompiler.compileInsert(
        schema,
        ordersTable,
        { values: { status: "x" }, children: {} },
        true,
      );
      expect(c.sql).toContain("RETURNING");
    });

    it("injection payload stays in parameter", () => {
      const payload = "'; DROP TABLE orders; --";
      const c = compiler.compileSelect(
        schema,
        q({ filter: comparison(fieldRef("status"), "eq", constant(payload)) }),
      );
      expect(c.sql).not.toContain("DROP");
      expect(c.parameters[0]!.value).toBe(payload);
    });
  });

  // ---- Type mapping ----

  describe("SQLite type mapping", () => {
    it("maps INTEGER to Edm.Int64", () => {
      expect(SqliteIntrospector.mapType("INTEGER")).toEqual({ edmType: "Edm.Int64", isFallback: false });
    });
    it("maps VARCHAR to Edm.String", () => {
      expect(SqliteIntrospector.mapType("VARCHAR(100)")).toEqual({
        edmType: "Edm.String",
        isFallback: false,
      });
    });
    it("maps BOOLEAN to Edm.Boolean", () => {
      expect(SqliteIntrospector.mapType("BOOLEAN")).toEqual({ edmType: "Edm.Boolean", isFallback: false });
    });
    it("maps DATETIME to Edm.DateTimeOffset", () => {
      expect(SqliteIntrospector.mapType("DATETIME")).toEqual({
        edmType: "Edm.DateTimeOffset",
        isFallback: false,
      });
    });
    it("maps DECIMAL to Edm.Decimal", () => {
      expect(SqliteIntrospector.mapType("DECIMAL(10,2)")).toEqual({
        edmType: "Edm.Decimal",
        isFallback: false,
      });
    });
    it("maps BLOB to Edm.Binary", () => {
      expect(SqliteIntrospector.mapType("BLOB")).toEqual({ edmType: "Edm.Binary", isFallback: false });
    });
  });

  describe("MySQL type mapping", () => {
    it("maps enum with values to Edm.String + allowedValues", () => {
      const result = MySqlIntrospector.mapType("enum", "enum('open','shipped','cancelled')");
      expect(result.edmType).toBe("Edm.String");
      expect(result.allowedValues).toEqual(["open", "shipped", "cancelled"]);
    });
    it("maps int unsigned to Edm.Int64", () => {
      expect(MySqlIntrospector.mapType("int", "int unsigned").edmType).toBe("Edm.Int64");
    });
    it("maps int to Edm.Int32", () => {
      expect(MySqlIntrospector.mapType("int", "int").edmType).toBe("Edm.Int32");
    });
    it("maps tinyint(1) to Edm.Boolean", () => {
      expect(MySqlIntrospector.mapType("tinyint", "tinyint(1)").edmType).toBe("Edm.Boolean");
    });
  });

  describe("SQL Server type mapping", () => {
    it("maps uniqueidentifier to Edm.Guid", () => {
      expect(SqlServerIntrospector.mapType("uniqueidentifier")).toEqual({
        edmType: "Edm.Guid",
        isFallback: false,
      });
    });
    it("maps datetime2 to Edm.DateTimeOffset", () => {
      expect(SqlServerIntrospector.mapType("datetime2")).toEqual({
        edmType: "Edm.DateTimeOffset",
        isFallback: false,
      });
    });
    it("maps money to Edm.Decimal", () => {
      expect(SqlServerIntrospector.mapType("money")).toEqual({ edmType: "Edm.Decimal", isFallback: false });
    });
    it("falls back to Edm.String for unknown types", () => {
      expect(SqlServerIntrospector.mapType("geography")).toEqual({ edmType: "Edm.String", isFallback: true });
    });
  });
});
