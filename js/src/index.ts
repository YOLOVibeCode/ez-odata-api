/**
 * @noctusoft/ezodata-express - bolt a governed OData v4 + REST + MCP API onto
 * any Express app. Pure TypeScript, full parity with the ez-odata .NET engine.
 *
 * The public surface grows layer by layer; for now the core domain and the
 * connector contracts are exported.
 */
export * from "./core/index.js";
export * from "./connectors/index.js";
