/**
 * REST filter parser (spec 06 §5) — re-exports the shared core implementation.
 * The core implementation lives in `src/core/filter-parser.ts` so both REST and
 * MCP can consume it without cross-protocol imports.
 */
export { parseFilter, FilterParseError } from "../core/filter-parser.js";
