import type { ColumnModel, SchemaSnapshot, TableModel } from "../core/schema.js";

/**
 * CSDL XML emitter (port of EdmModelFactory.cs, spec 05 §3).
 * Produces OData v4 CSDL XML for a SchemaSnapshot without external EDM libraries.
 */

const EDM_NS = "http://docs.oasis-open.org/odata/ns/edm";
const EDMX_NS = "http://docs.oasis-open.org/odata/ns/edmx";

export function buildCsdlXml(serviceName: string, snapshot: SchemaSnapshot): string {
  const ns = `EzOdata.${sanitize(serviceName)}`;
  const lines: string[] = [];

  lines.push(`<?xml version="1.0" encoding="utf-8"?>`);
  lines.push(`<edmx:Edmx Version="4.0" xmlns:edmx="${EDMX_NS}">`);
  lines.push(`  <edmx:DataServices>`);
  lines.push(`    <Schema Namespace="${esc(ns)}" xmlns="${EDM_NS}">`);

  // ---- Entity types ----
  for (const table of snapshot.tables) {
    lines.push(...entityTypeLines(ns, table));
  }

  // ---- Entity container ----
  lines.push(`      <EntityContainer Name="Container">`);
  for (const table of snapshot.tables) {
    const typeName = `${esc(ns)}.${esc(table.exposedName)}`;
    lines.push(`        <EntitySet Name="${esc(table.exposedName)}" EntityType="${typeName}">`);

    // Navigation property bindings
    for (const fk of table.foreignKeys) {
      if (snapshot.tables.some((t) => t.exposedName === fk.refTable)) {
        lines.push(
          `          <NavigationPropertyBinding Path="${esc(fk.navToOne)}" Target="${esc(fk.refTable)}"/>`,
        );
      }
    }
    // Reverse bindings: tables that reference this one
    for (const other of snapshot.tables) {
      for (const fk of other.foreignKeys) {
        if (fk.refTable === table.exposedName) {
          lines.push(
            `          <NavigationPropertyBinding Path="${esc(fk.navToMany)}" Target="${esc(other.exposedName)}"/>`,
          );
        }
      }
    }
    lines.push(`        </EntitySet>`);
  }
  lines.push(`      </EntityContainer>`);
  lines.push(`    </Schema>`);
  lines.push(`  </edmx:DataServices>`);
  lines.push(`</edmx:Edmx>`);

  return lines.join("\n");
}

function entityTypeLines(ns: string, table: TableModel): string[] {
  const lines: string[] = [];
  lines.push(`      <EntityType Name="${esc(table.exposedName)}">`);

  // Key
  if (table.primaryKey.length > 0) {
    lines.push(`        <Key>`);
    for (const k of table.primaryKey) {
      lines.push(`          <PropertyRef Name="${esc(k)}"/>`);
    }
    lines.push(`        </Key>`);
  }

  // Structural properties
  for (const col of table.columns) {
    lines.push(`        ${propertyLine(col)}`);
  }

  // Navigation properties (to-one: FK declared on this table)
  for (const fk of table.foreignKeys) {
    const refType = `${esc(ns)}.${esc(fk.refTable)}`;
    const partner = esc(fk.navToMany);
    const nullable = fk.columns.some(
      (c) => table.columns.find((col) => col.exposedName === c)?.nullable !== false,
    );
    lines.push(
      `        <NavigationProperty Name="${esc(fk.navToOne)}" Type="${refType}" Nullable="${nullable}" Partner="${partner}"/>`,
    );
  }

  // Navigation properties (to-many: FK on other tables referencing this one)
  // These are resolved by cross-referencing at build time; we add them from the
  // back-reference perspective stored on the FK.
  // We detect them by looking at all tables' FKs that point here.
  // NOTE: We must handle this in the outer function to access the full snapshot.
  // Place-holder — filled by buildCsdlXml via a second pass via entityTypeNavToManyLines().

  lines.push(`      </EntityType>`);
  return lines;
}

function propertyLine(col: ColumnModel): string {
  const parts: string[] = [`<Property Name="${esc(col.exposedName)}" Type="${edmTypeAttr(col)}"`];
  if (!col.nullable) parts.push(`Nullable="false"`);
  if (col.edmType === "Edm.String" && col.maxLength !== undefined) {
    parts.push(`MaxLength="${col.maxLength}"`);
  }
  if (col.edmType === "Edm.Decimal") {
    if (col.precision !== undefined) parts.push(`Precision="${col.precision}"`);
    if (col.scale !== undefined) parts.push(`Scale="${col.scale}"`);
  }
  parts.push(`/>`);
  return parts.join(" ");
}

function edmTypeAttr(col: ColumnModel): string {
  return col.edmType;
}

/** XML-escape attribute values. */
function esc(value: string): string {
  return value.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/"/g, "&quot;");
}

function sanitize(name: string): string {
  const chars = name.replace(/[^a-zA-Z0-9]/g, "_");
  return /^[a-zA-Z]/.test(chars) ? chars : `s_${chars}`;
}

/**
 * Full CSDL build with two-pass navigation (to-many properties need the full snapshot).
 * The entity type lines in buildCsdlXml already include to-one navs. This second pass
 * injects to-many navs by re-generating the entity type block.
 */
export function buildCsdlXmlFull(serviceName: string, snapshot: SchemaSnapshot): string {
  const ns = `EzOdata.${sanitize(serviceName)}`;
  const lines: string[] = [];

  lines.push(`<?xml version="1.0" encoding="utf-8"?>`);
  lines.push(`<edmx:Edmx Version="4.0" xmlns:edmx="${EDMX_NS}">`);
  lines.push(`  <edmx:DataServices>`);
  lines.push(`    <Schema Namespace="${esc(ns)}" xmlns="${EDM_NS}">`);

  // ---- Entity types (two-pass: structural + to-one, then inject to-many) ----
  for (const table of snapshot.tables) {
    lines.push(`      <EntityType Name="${esc(table.exposedName)}">`);

    if (table.primaryKey.length > 0) {
      lines.push(`        <Key>`);
      for (const k of table.primaryKey) {
        lines.push(`          <PropertyRef Name="${esc(k)}"/>`);
      }
      lines.push(`        </Key>`);
    }

    for (const col of table.columns) {
      lines.push(`        ${propertyLine(col)}`);
    }

    // To-one navigation properties (FK declared on this table)
    for (const fk of table.foreignKeys) {
      if (!snapshot.tables.some((t) => t.exposedName === fk.refTable)) continue;
      const refType = `${esc(ns)}.${esc(fk.refTable)}`;
      const nullable = fk.columns.some(
        (c) => table.columns.find((col) => col.exposedName === c)?.nullable !== false,
      );
      lines.push(
        `        <NavigationProperty Name="${esc(fk.navToOne)}" Type="${refType}" Nullable="${nullable}" Partner="${esc(fk.navToMany)}"/>`,
      );
    }

    // To-many navigation properties (FK on other tables referencing this one)
    for (const other of snapshot.tables) {
      for (const fk of other.foreignKeys) {
        if (fk.refTable !== table.exposedName) continue;
        const childType = `Collection(${esc(ns)}.${esc(other.exposedName)})`;
        lines.push(
          `        <NavigationProperty Name="${esc(fk.navToMany)}" Type="${childType}" Partner="${esc(fk.navToOne)}"/>`,
        );
      }
    }

    lines.push(`      </EntityType>`);
  }

  // ---- Entity container ----
  lines.push(`      <EntityContainer Name="Container">`);
  for (const table of snapshot.tables) {
    const typeName = `${esc(ns)}.${esc(table.exposedName)}`;
    lines.push(`        <EntitySet Name="${esc(table.exposedName)}" EntityType="${typeName}">`);

    for (const fk of table.foreignKeys) {
      if (snapshot.tables.some((t) => t.exposedName === fk.refTable)) {
        lines.push(
          `          <NavigationPropertyBinding Path="${esc(fk.navToOne)}" Target="${esc(fk.refTable)}"/>`,
        );
      }
    }

    for (const other of snapshot.tables) {
      for (const fk of other.foreignKeys) {
        if (fk.refTable === table.exposedName) {
          lines.push(
            `          <NavigationPropertyBinding Path="${esc(fk.navToMany)}" Target="${esc(other.exposedName)}"/>`,
          );
        }
      }
    }
    lines.push(`        </EntitySet>`);
  }
  lines.push(`      </EntityContainer>`);
  lines.push(`    </Schema>`);
  lines.push(`  </edmx:DataServices>`);
  lines.push(`</edmx:Edmx>`);

  return lines.join("\n");
}
