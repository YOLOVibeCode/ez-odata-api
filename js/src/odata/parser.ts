/**
 * OData v4 URL query-option parser (port of the ODataUriParser + ODataAstTranslator pipeline,
 * spec 05 §4). Parses $filter, $select, $orderby, $top, $skip, $count, $expand, $apply,
 * $search, $skiptoken from a URL search string into Query IR.
 *
 * Pure TypeScript recursive-descent parser; no ODL dependency.
 */

import {
  comparison,
  constant,
  fieldArg,
  fieldRef,
  fn,
  inList,
  lambda,
  logical,
  not,
  constantArg,
  type AggregateOp,
  type Aggregation,
  type ApplyClause,
  type ComparisonOp,
  type ExpandNode,
  type FilterArg,
  type FilterFunction,
  type FilterNode,
  type OrderByItem,
} from "../core/query.js";

export class ODataParseError extends Error {
  constructor(message: string) {
    super(message);
    this.name = "ODataParseError";
  }
}

export interface ParsedQuery {
  filter?: FilterNode;
  select?: readonly string[];
  orderBy: readonly OrderByItem[];
  top?: number;
  skip?: number;
  count: boolean;
  expand: readonly ExpandNode[];
  apply?: ApplyClause;
  applyFilter?: FilterNode;
  search?: string;
  skipToken?: string;
}

/** Parse all OData query options from a URL query-string (without the leading `?`). */
export function parseQueryOptions(queryString: string): ParsedQuery {
  const params = parseQueryParams(queryString);

  const result: ParsedQuery = {
    orderBy: [],
    count: false,
    expand: [],
  };

  if (params.$filter) {
    result.filter = parseFilter(params.$filter);
  }
  if (params.$select) {
    result.select = params.$select
      .split(",")
      .map((s) => s.trim())
      .filter(Boolean);
  }
  if (params.$orderby) {
    result.orderBy = parseOrderBy(params.$orderby);
  }
  if (params.$top !== undefined) {
    const n = parseInt(params.$top, 10);
    if (isNaN(n) || n < 0) throw new ODataParseError(`Invalid $top: ${params.$top}`);
    result.top = n;
  }
  if (params.$skip !== undefined) {
    const n = parseInt(params.$skip, 10);
    if (isNaN(n) || n < 0) throw new ODataParseError(`Invalid $skip: ${params.$skip}`);
    result.skip = n;
  }
  if (params.$count !== undefined) {
    result.count = params.$count === "true";
  }
  if (params.$expand) {
    result.expand = parseExpand(params.$expand, 1, 3);
  }
  if (params.$apply) {
    const { clause, preFilter } = parseApply(params.$apply);
    result.apply = clause;
    if (preFilter !== undefined) result.applyFilter = preFilter;
  }
  if (params.$search) {
    result.search = params.$search;
  }
  if (params.$skiptoken) {
    result.skipToken = params.$skiptoken;
  }

  return result;
}

// ---- Query-string splitting ----

function parseQueryParams(qs: string): Record<string, string> {
  const result: Record<string, string> = {};
  if (!qs) return result;
  // Split by & but respect parentheses depth for $expand etc.
  const parts = splitAtDepth(qs, "&");
  for (const part of parts) {
    const eq = part.indexOf("=");
    if (eq < 0) continue;
    const key = decodeURIComponent(part.slice(0, eq));
    const value = decodeURIComponent(part.slice(eq + 1));
    result[key] = value;
  }
  return result;
}

// ---- $orderby parser ----

function parseOrderBy(input: string): readonly OrderByItem[] {
  const items: OrderByItem[] = [];
  for (const part of input.split(",")) {
    const trimmed = part.trim();
    if (!trimmed) continue;
    const tokens = trimmed.split(/\s+/);
    const field = tokens[0]!;
    const dir = (tokens[1] ?? "asc").toLowerCase();
    items.push({ field, descending: dir === "desc" });
  }
  return items;
}

// ---- $expand parser ----

export function parseExpand(
  input: string,
  depth: number,
  maxDepth: number,
): readonly ExpandNode[] {
  if (depth > maxDepth) {
    throw new ODataParseError(`$expand depth is limited to ${maxDepth}.`);
  }

  const nodes: ExpandNode[] = [];
  // Split by comma at depth 0 (commas inside parentheses are part of nested options)
  const parts = splitAtDepth(input, ",");
  for (const part of parts.map((s) => s.trim()).filter(Boolean)) {
    nodes.push(parseExpandItem(part, depth, maxDepth));
  }
  return nodes;
}

function parseExpandItem(input: string, depth: number, maxDepth: number): ExpandNode {
  const parenIdx = input.indexOf("(");
  if (parenIdx < 0) {
    // Simple navigation, no options
    return {
      navigation: input.trim(),
      orderBy: [],
      expand: [],
    };
  }

  const navigation = input.slice(0, parenIdx).trim();
  const optionsStr = input.slice(parenIdx + 1, findMatchingParen(input, parenIdx));

  const opts = parseExpandOptions(optionsStr, depth + 1, maxDepth);
  return {
    navigation,
    orderBy: opts.orderBy ?? [],
    expand: opts.expand ?? [],
    ...(opts.filter !== undefined ? { filter: opts.filter } : {}),
    ...(opts.select !== undefined ? { select: opts.select } : {}),
    ...(opts.top !== undefined ? { top: opts.top } : {}),
    ...(opts.skip !== undefined ? { skip: opts.skip } : {}),
  };
}

interface ExpandOptions {
  filter?: FilterNode;
  select?: readonly string[];
  orderBy?: readonly OrderByItem[];
  expand?: readonly ExpandNode[];
  top?: number;
  skip?: number;
}

function parseExpandOptions(input: string, depth: number, maxDepth: number): ExpandOptions {
  const opts: ExpandOptions = {};
  const parts = splitAtDepth(input, ";");

  for (const part of parts.map((s) => s.trim()).filter(Boolean)) {
    const eqIdx = part.indexOf("=");
    if (eqIdx < 0) continue;
    const key = part.slice(0, eqIdx).trim();
    const value = part.slice(eqIdx + 1).trim();

    switch (key) {
      case "$filter":
        opts.filter = parseFilter(value);
        break;
      case "$select":
        opts.select = value
          .split(",")
          .map((s) => s.trim())
          .filter(Boolean);
        break;
      case "$orderby":
        opts.orderBy = parseOrderBy(value);
        break;
      case "$top":
        opts.top = parseInt(value, 10);
        break;
      case "$skip":
        opts.skip = parseInt(value, 10);
        break;
      case "$expand":
        opts.expand = parseExpand(value, depth, maxDepth);
        break;
    }
  }
  return opts;
}

// ---- $apply parser ----

export interface ApplyParseResult {
  clause: ApplyClause;
  preFilter?: FilterNode;
}

export function parseApply(input: string): ApplyParseResult {
  const transformations = splitAtDepth(input, "/");

  const groupBy: string[] = [];
  const aggregations: Aggregation[] = [];
  let preFilter: FilterNode | undefined;

  for (const raw of transformations.map((s) => s.trim()).filter(Boolean)) {
    if (raw.startsWith("filter(")) {
      const inner = raw.slice("filter(".length, findMatchingParen(raw, "filter(".length - 1));
      preFilter = parseFilter(inner);
    } else if (raw.startsWith("groupby(")) {
      const inner = raw.slice("groupby(".length, findMatchingParen(raw, "groupby(".length - 1));
      parseGroupBy(inner, groupBy, aggregations);
    } else if (raw.startsWith("aggregate(")) {
      const inner = raw.slice("aggregate(".length, findMatchingParen(raw, "aggregate(".length - 1));
      parseAggregations(inner, aggregations);
    }
  }

  return {
    clause: { groupBy, aggregations },
    ...(preFilter !== undefined ? { preFilter } : {}),
  };
}

function parseGroupBy(inner: string, groupBy: string[], aggregations: Aggregation[]): void {
  // groupby((field1, field2), aggregate(...))
  // inner = "(field1, field2), aggregate(...)"
  const firstParen = inner.indexOf("(");
  if (firstParen < 0) return;
  const closeParen = findMatchingParen(inner, firstParen);
  const fields = inner
    .slice(firstParen + 1, closeParen)
    .split(",")
    .map((s) => s.trim())
    .filter(Boolean);
  groupBy.push(...fields);

  const rest = inner.slice(closeParen + 1).trim();
  if (rest.startsWith(",")) {
    const aggStr = rest.slice(1).trim();
    if (aggStr.startsWith("aggregate(")) {
      const aggInner = aggStr.slice("aggregate(".length, findMatchingParen(aggStr, "aggregate(".length - 1));
      parseAggregations(aggInner, aggregations);
    }
  }
}

function parseAggregations(inner: string, aggregations: Aggregation[]): void {
  // "field with sum as alias, $count as n"
  const parts = splitAtDepth(inner, ",");
  for (const part of parts.map((s) => s.trim()).filter(Boolean)) {
    if (part.startsWith("$count")) {
      // $count as alias
      const asIdx = part.toLowerCase().indexOf(" as ");
      const alias = asIdx >= 0 ? part.slice(asIdx + 4).trim() : "count";
      aggregations.push({ op: "count", alias });
    } else {
      // field with method as alias
      const withIdx = part.toLowerCase().indexOf(" with ");
      const asIdx = part.toLowerCase().indexOf(" as ");
      if (withIdx < 0 || asIdx < 0) continue;
      const field = part.slice(0, withIdx).trim();
      const method = part.slice(withIdx + 6, asIdx).trim().toLowerCase();
      const alias = part.slice(asIdx + 4).trim();
      const op = parseAggMethod(method);
      if (op) aggregations.push({ op, field, alias });
    }
  }
}

function parseAggMethod(method: string): AggregateOp | undefined {
  switch (method) {
    case "sum":
      return "sum";
    case "average":
    case "avg":
      return "average";
    case "min":
      return "min";
    case "max":
      return "max";
    case "countdistinct":
      return "countDistinct";
    case "count":
      return "count";
    default:
      return undefined;
  }
}

// ---- $filter parser ----

export function parseFilter(input: string): FilterNode {
  const tokens = tokenize(input);
  const parser = new FilterParser(tokens);
  const node = parser.parseBoolExpr();
  if (!parser.atEnd()) {
    throw new ODataParseError(`Unexpected token at position ${parser.pos}: '${parser.peek()}'`);
  }
  return node;
}

// ---- Tokenizer ----

type TokKind = "ident" | "string" | "number" | "lparen" | "rparen" | "comma" | "slash" | "colon" | "eof";

interface Token {
  kind: TokKind;
  value: string;
}


function tokenize(input: string): Token[] {
  const tokens: Token[] = [];
  let i = 0;

  while (i < input.length) {
    // Skip whitespace
    while (i < input.length && /\s/.test(input[i]!)) i++;
    if (i >= input.length) break;

    const ch = input[i]!;

    if (ch === "(") {
      tokens.push({ kind: "lparen", value: "(" });
      i++;
    } else if (ch === ")") {
      tokens.push({ kind: "rparen", value: ")" });
      i++;
    } else if (ch === ",") {
      tokens.push({ kind: "comma", value: "," });
      i++;
    } else if (ch === "/") {
      tokens.push({ kind: "slash", value: "/" });
      i++;
    } else if (ch === ":") {
      tokens.push({ kind: "colon", value: ":" });
      i++;
    } else if (ch === "'") {
      // String literal — handle '' escape
      let str = "";
      i++;
      while (i < input.length) {
        if (input[i] === "'") {
          if (input[i + 1] === "'") {
            str += "'";
            i += 2;
          } else {
            i++;
            break;
          }
        } else {
          str += input[i];
          i++;
        }
      }
      tokens.push({ kind: "string", value: str });
    } else if (/[0-9]/.test(ch) || (ch === "-" && /[0-9]/.test(input[i + 1] ?? ""))) {
      let num = ch;
      i++;
      while (i < input.length && /[0-9.]/.test(input[i]!)) {
        num += input[i];
        i++;
      }
      tokens.push({ kind: "number", value: num });
    } else if (/[a-zA-Z_$]/.test(ch)) {
      let ident = "";
      while (i < input.length && /[a-zA-Z0-9_$]/.test(input[i]!)) {
        ident += input[i];
        i++;
      }
      tokens.push({ kind: "ident", value: ident });
    } else {
      // Skip unknown characters (handles whitespace edge cases)
      i++;
    }
  }

  tokens.push({ kind: "eof", value: "" });
  return tokens;
}

// ---- Recursive descent parser ----

class FilterParser {
  pos = 0;
  private readonly tokens: Token[];

  constructor(tokens: Token[]) {
    this.tokens = tokens;
  }

  peek(): string {
    return this.tokens[this.pos]?.value ?? "";
  }

  peekKind(): TokKind {
    return this.tokens[this.pos]?.kind ?? "eof";
  }

  consume(): Token {
    const tok = this.tokens[this.pos]!;
    this.pos++;
    return tok;
  }

  atEnd(): boolean {
    return this.tokens[this.pos]?.kind === "eof";
  }

  expect(value: string): void {
    const tok = this.consume();
    if (tok.value !== value) {
      throw new ODataParseError(`Expected '${value}' but got '${tok.value}'`);
    }
  }

  parseBoolExpr(): FilterNode {
    return this.parseOr();
  }

  private parseOr(): FilterNode {
    let left = this.parseAnd();
    while (this.peekKind() === "ident" && this.peek().toLowerCase() === "or") {
      this.consume();
      const right = this.parseAnd();
      left = logical("or", [left, right]);
    }
    return left;
  }

  private parseAnd(): FilterNode {
    let left = this.parseNot();
    while (this.peekKind() === "ident" && this.peek().toLowerCase() === "and") {
      this.consume();
      const right = this.parseNot();
      left = logical("and", [left, right]);
    }
    return left;
  }

  private parseNot(): FilterNode {
    if (this.peekKind() === "ident" && this.peek().toLowerCase() === "not") {
      this.consume();
      return not(this.parseNot());
    }
    return this.parseAtom();
  }

  private parseAtom(): FilterNode {
    // Parenthesized group
    if (this.peekKind() === "lparen") {
      this.consume(); // (
      const expr = this.parseBoolExpr();
      this.expect(")");
      return expr;
    }

    // Must be an identifier (field path or function name)
    if (this.peekKind() !== "ident") {
      throw new ODataParseError(`Expected expression but got '${this.peek()}'`);
    }

    return this.parseIdentExpr();
  }

  /** Parse an expression starting with an identifier. */
  private parseIdentExpr(rangeVar?: string): FilterNode {
    // Collect a dotted/slashed path, watching for function calls and lambdas
    const firstIdent = this.consume().value; // identifier

    // Is this a function call? (ident followed by '(')
    if (this.peekKind() === "lparen" && isFunctionName(firstIdent)) {
      return this.parseFunctionExpr(firstIdent);
    }

    // Build a field path (possibly navigated: a/b/c)
    const path: string[] = [rangeVar && firstIdent === rangeVar ? "" : firstIdent];
    // If the identifier equals the range variable, it acts as a prefix — we track below

    let isRangeVarPrefix = rangeVar !== undefined && firstIdent === rangeVar;

    while (this.peekKind() === "slash") {
      this.consume(); // /
      if (this.peekKind() !== "ident") {
        throw new ODataParseError("Expected identifier after '/'");
      }
      const next = this.peek();

      // Lambda operator?
      if (next === "any" || next === "all") {
        this.consume();
        return this.parseLambda(isRangeVarPrefix ? [] : path, next as "any" | "all");
      }

      this.consume();
      if (isRangeVarPrefix) {
        // The first segment was the range variable — replace it
        path[0] = next;
        isRangeVarPrefix = false;
      } else {
        path.push(next);
      }
    }

    // Clean up: if we started with the range variable, path[0] is the field
    const cleanPath = isRangeVarPrefix ? path.slice(1) : path;
    if (cleanPath.length === 0) {
      throw new ODataParseError("Empty field path");
    }

    const field = fieldRef(...cleanPath);

    // Check what follows: comparison op, 'in', or end-of-expression
    const nextLower = this.peek().toLowerCase();

    if (nextLower === "in") {
      this.consume(); // in
      return this.parseInList(field);
    }

    if (isCompOp(nextLower)) {
      const op = nextLower as ComparisonOp;
      this.consume();
      const val = this.parseLiteralValue();
      return comparison(field, op, val);
    }

    // Bool function or bare field ref (shouldn't appear in typical OData) — error
    throw new ODataParseError(`Expected comparison operator after field '${cleanPath.join("/")}' but got '${this.peek()}'`);
  }

  private parseFunctionExpr(funcName: string): FilterNode {
    this.expect("(");
    const args: FilterArg[] = [];
    while (this.peek() !== ")") {
      args.push(this.parseFunctionArg());
      if (this.peek() === ",") this.consume();
    }
    this.expect(")");

    const func = mapFunctionName(funcName);

    // Check for comparison operator (e.g., year(created_at) eq 2026)
    const nextLower = this.peek().toLowerCase();
    if (isCompOp(nextLower)) {
      const op = nextLower as ComparisonOp;
      this.consume();
      const comparand = this.parseLiteralValue();
      return fn(func, args, op, comparand);
    }

    // Boolean function (contains, startswith, endswith)
    return fn(func, args);
  }

  private parseFunctionArg(): FilterArg {
    if (this.peekKind() === "string") {
      return constantArg(constant(this.consume().value));
    }
    if (this.peekKind() === "number") {
      return constantArg(constant(parseNumber(this.consume().value)));
    }
    if (this.peekKind() === "ident") {
      const val = this.peek().toLowerCase();
      if (val === "null") { this.consume(); return constantArg(constant(null)); }
      if (val === "true") { this.consume(); return constantArg(constant(true)); }
      if (val === "false") { this.consume(); return constantArg(constant(false)); }
      // Field path
      const path = this.parseFieldPath();
      return fieldArg(fieldRef(...path));
    }
    throw new ODataParseError(`Unexpected function argument: '${this.peek()}'`);
  }

  private parseFieldPath(): string[] {
    const path: string[] = [];
    while (this.peekKind() === "ident") {
      path.push(this.consume().value);
      if (this.peekKind() === "slash") {
        this.consume();
      } else {
        break;
      }
    }
    return path;
  }

  private parseLambda(path: string[], kind: "any" | "all"): FilterNode {
    const navigation = path.join("/");
    this.expect("(");

    // Empty any() — bare lambda
    if (this.peek() === ")") {
      this.consume();
      return lambda(navigation, kind);
    }

    // Parse range variable: ident ':'
    if (this.peekKind() !== "ident") {
      throw new ODataParseError("Expected range variable in lambda");
    }
    const rangeVar = this.consume().value;
    this.expect(":");

    // Parse predicate with range variable context
    const predicate = this.parseLambdaPredicate(rangeVar);
    this.expect(")");
    return lambda(navigation, kind, predicate);
  }

  /** Parse a filter expression inside a lambda, where the range variable prefix is stripped. */
  private parseLambdaPredicate(rangeVar: string): FilterNode {
    return this.parseLambdaOr(rangeVar);
  }

  private parseLambdaOr(rangeVar: string): FilterNode {
    let left = this.parseLambdaAnd(rangeVar);
    while (this.peekKind() === "ident" && this.peek().toLowerCase() === "or") {
      this.consume();
      const right = this.parseLambdaAnd(rangeVar);
      left = logical("or", [left, right]);
    }
    return left;
  }

  private parseLambdaAnd(rangeVar: string): FilterNode {
    let left = this.parseLambdaNot(rangeVar);
    while (this.peekKind() === "ident" && this.peek().toLowerCase() === "and") {
      this.consume();
      const right = this.parseLambdaNot(rangeVar);
      left = logical("and", [left, right]);
    }
    return left;
  }

  private parseLambdaNot(rangeVar: string): FilterNode {
    if (this.peekKind() === "ident" && this.peek().toLowerCase() === "not") {
      this.consume();
      return not(this.parseLambdaNot(rangeVar));
    }
    return this.parseLambdaAtom(rangeVar);
  }

  private parseLambdaAtom(rangeVar: string): FilterNode {
    if (this.peekKind() === "lparen") {
      this.consume();
      const expr = this.parseLambdaPredicate(rangeVar);
      this.expect(")");
      return expr;
    }
    return this.parseIdentExpr(rangeVar);
  }

  private parseInList(field: ReturnType<typeof fieldRef>): FilterNode {
    this.expect("(");
    const values: ReturnType<typeof constant>[] = [];
    while (this.peek() !== ")") {
      values.push(this.parseLiteralValue());
      if (this.peek() === ",") this.consume();
    }
    this.expect(")");
    return inList(field, values);
  }

  parseLiteralValue(): ReturnType<typeof constant> {
    const tok = this.tokens[this.pos]!;
    switch (tok.kind) {
      case "string":
        this.consume();
        return constant(tok.value);
      case "number":
        this.consume();
        return constant(parseNumber(tok.value));
      case "ident": {
        const low = tok.value.toLowerCase();
        if (low === "null") { this.consume(); return constant(null); }
        if (low === "true") { this.consume(); return constant(true); }
        if (low === "false") { this.consume(); return constant(false); }
        throw new ODataParseError(`Expected literal value but got identifier '${tok.value}'`);
      }
      default:
        throw new ODataParseError(`Expected literal value but got '${tok.value}'`);
    }
  }
}

// ---- Helpers ----

function isCompOp(s: string): boolean {
  return s === "eq" || s === "ne" || s === "gt" || s === "ge" || s === "lt" || s === "le";
}

function isFunctionName(s: string): boolean {
  const funcs = new Set([
    "contains", "startswith", "endswith", "tolower", "toupper", "trim",
    "length", "indexof", "substring", "concat",
    "year", "month", "day", "hour", "minute", "second", "date", "time", "now",
    "round", "floor", "ceiling",
  ]);
  return funcs.has(s.toLowerCase());
}

function mapFunctionName(s: string): FilterFunction {
  switch (s.toLowerCase()) {
    case "contains": return "contains";
    case "startswith": return "startsWith";
    case "endswith": return "endsWith";
    case "tolower": return "toLower";
    case "toupper": return "toUpper";
    case "trim": return "trim";
    case "length": return "length";
    case "indexof": return "indexOf";
    case "substring": return "substring";
    case "concat": return "concat";
    case "year": return "year";
    case "month": return "month";
    case "day": return "day";
    case "hour": return "hour";
    case "minute": return "minute";
    case "second": return "second";
    case "date": return "date";
    case "time": return "time";
    case "now": return "now";
    case "round": return "round";
    case "floor": return "floor";
    case "ceiling": return "ceiling";
    default: throw new ODataParseError(`Unknown function '${s}'`);
  }
}

function parseNumber(s: string): number {
  const n = s.includes(".") ? parseFloat(s) : parseInt(s, 10);
  if (isNaN(n)) throw new ODataParseError(`Invalid number: ${s}`);
  return n;
}

/** Split `input` by `delimiter` at depth 0 (respecting string literals and parentheses). */
function splitAtDepth(input: string, delimiter: string): string[] {
  const parts: string[] = [];
  let depth = 0;
  let inString = false;
  let start = 0;

  for (let i = 0; i < input.length; i++) {
    const ch = input[i]!;
    if (ch === "'" && !inString) {
      inString = true;
    } else if (ch === "'" && inString) {
      if (input[i + 1] === "'") {
        i++; // escaped quote
      } else {
        inString = false;
      }
    }
    if (!inString) {
      if (ch === "(") depth++;
      else if (ch === ")") depth--;
      else if (depth === 0 && input.slice(i, i + delimiter.length) === delimiter) {
        parts.push(input.slice(start, i));
        start = i + delimiter.length;
        i += delimiter.length - 1;
      }
    }
  }
  parts.push(input.slice(start));
  return parts;
}

/** Find the index of the closing paren matching the one at `openIdx`. */
function findMatchingParen(input: string, openIdx: number): number {
  let depth = 0;
  let inString = false;
  for (let i = openIdx; i < input.length; i++) {
    const ch = input[i]!;
    if (ch === "'" && !inString) inString = true;
    else if (ch === "'" && inString) {
      if (input[i + 1] === "'") i++;
      else inString = false;
    }
    if (!inString) {
      if (ch === "(") depth++;
      else if (ch === ")") {
        depth--;
        if (depth === 0) return i;
      }
    }
  }
  throw new ODataParseError("Unmatched parenthesis");
}

// ---- URL path parsing (entity set, key, $count) ----

export interface ParsedPath {
  entitySet: string;
  /** Parsed key values (undefined = collection request). */
  key?: Record<string, unknown>;
  isCount: boolean;
}

/** Parse an OData resource path like "customers", "customers(1)", "customers(id=1,name='x')", "customers/$count". */
export function parsePath(path: string): ParsedPath {
  // Strip leading /
  let p = path.startsWith("/") ? path.slice(1) : path;

  let isCount = false;
  if (p.endsWith("/$count")) {
    isCount = true;
    p = p.slice(0, -"/$count".length);
  }

  const parenIdx = p.indexOf("(");
  if (parenIdx < 0) {
    return { entitySet: p, isCount };
  }

  const entitySet = p.slice(0, parenIdx);
  const keyStr = p.slice(parenIdx + 1, p.lastIndexOf(")"));
  const key = parseKey(keyStr);
  return { entitySet, key, isCount };
}

function parseKey(keyStr: string): Record<string, unknown> {
  const key: Record<string, unknown> = {};

  // Named pairs: order_id=1,line_no=2
  if (keyStr.includes("=")) {
    const pairs = splitAtDepth(keyStr, ",");
    for (const pair of pairs) {
      const eqIdx = pair.indexOf("=");
      const name = pair.slice(0, eqIdx).trim();
      const valStr = pair.slice(eqIdx + 1).trim();
      key[name] = parseLiteralStr(valStr);
    }
  } else {
    // Positional: customers(1) — use placeholder "__key__" resolved by handler
    key["__positional__"] = parseLiteralStr(keyStr.trim());
  }

  return key;
}

function parseLiteralStr(s: string): unknown {
  if (s === "null") return null;
  if (s === "true") return true;
  if (s === "false") return false;
  if (s.startsWith("'") && s.endsWith("'")) return s.slice(1, -1).replace(/''/g, "'");
  const n = s.includes(".") ? parseFloat(s) : parseInt(s, 10);
  if (!isNaN(n)) return n;
  return s;
}
