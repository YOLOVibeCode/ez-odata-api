/**
 * Layering guard - the TypeScript analog of the .NET NetArchTest rules
 * (tests/EzOdata.UnitTests/Architecture/LayeringTests.cs).
 *
 * Layers (inner -> outer): core -> connectors -> {odata, rest, mcp, openapi} -> express
 *   - core depends on nothing else in src
 *   - connectors depend only on core
 *   - protocol modules depend on core/connectors, never on each other
 *   - express (composition root) may depend on everything
 */
module.exports = {
  forbidden: [
    {
      name: "core-is-self-contained",
      comment: "core/ is pure logic and must not import any outer layer.",
      severity: "error",
      from: { path: "^src/core/" },
      to: { path: "^src/(connectors|odata|rest|mcp|openapi|express)/" },
    },
    {
      name: "connectors-depend-only-on-core",
      comment: "connectors/ may use core/ but no protocol/host layer.",
      severity: "error",
      from: { path: "^src/connectors/" },
      to: { path: "^src/(odata|rest|mcp|openapi|express)/" },
    },
    {
      name: "protocols-do-not-cross-import",
      comment: "odata/rest/mcp/openapi must stay independent of each other.",
      severity: "error",
      from: { path: "^src/(odata|rest|mcp|openapi)/" },
      to: {
        path: "^src/(odata|rest|mcp|openapi)/",
        // $1 is the protocol matched in `from`; exclude same-module imports.
        pathNot: "^src/$1/",
      },
    },
    {
      name: "no-circular",
      comment: "No circular dependencies anywhere.",
      severity: "error",
      from: {},
      to: { circular: true },
    },
    {
      name: "no-orphans",
      severity: "warn",
      from: { orphan: true, pathNot: ["\\.d\\.ts$", "src/index\\.ts$"] },
      to: {},
    },
  ],
  options: {
    doNotFollow: { path: "node_modules" },
    tsConfig: { fileName: "tsconfig.json" },
    tsPreCompilationDeps: true,
    enhancedResolveOptions: {
      extensions: [".ts", ".js"],
    },
  },
};
