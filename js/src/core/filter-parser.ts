/**
 * REST/MCP SQL-ish filter grammar (port of RestFilterParser.cs, spec 06 §5).
 * Parses to the shared FilterNode IR — never concatenated into SQL.
 *
 * Grammar:
 *   expr       := orExpr
 *   orExpr     := andExpr ("or" andExpr)*
 *   andExpr    := term ("and" term)*
 *   term       := "not"? (comparison | "(" expr ")")
 *   comparison := field op value | field "in" "(" value ("," value)* ")"
 *                | field ("is null" | "is not null") | field "like" string
 */

import { ErrorCodes } from "./errors.js";
import {
  comparison,
  constant,
  fieldRef,
  fn,
  inList,
  logical,
  not,
  NULL_CONSTANT,
  type FilterNode,
  type FilterFunction,
} from "./query.js";

export class FilterParseError extends Error {
  readonly errorCode: string;
  constructor(errorCode: string, message: string) {
    super(message);
    this.name = "FilterParseError";
    this.errorCode = errorCode;
  }
}

export function parseFilter(input: string): FilterNode {
  const tokens = tokenize(input);
  const parser = new Parser(tokens);
  const node = parser.parseOr();
  parser.expect(TokenKind.End);
  return node;
}

// ---- Token types ----

enum TokenKind {
  Identifier = 0,
  Keyword = 1,
  Operator = 2,
  String = 3,
  Number = 4,
  LParen = 5,
  RParen = 6,
  Comma = 7,
  Dot = 8,
  End = 9,
}

interface Token {
  kind: TokenKind;
  text: string;
}

const KEYWORDS = new Set([
  "and", "or", "not", "in", "is", "null", "like", "true", "false",
  "contains", "starts", "ends", "with",
]);

// ---- Parser class ----

class Parser {
  private pos = 0;

  constructor(private readonly tokens: Token[]) {}

  parseOr(): FilterNode {
    let left = this.parseAnd();
    while (this.tryConsumeKeyword("or")) {
      left = logical("or", [left, this.parseAnd()]);
    }
    return left;
  }

  private parseAnd(): FilterNode {
    let left = this.parseTerm();
    while (this.tryConsumeKeyword("and")) {
      left = logical("and", [left, this.parseTerm()]);
    }
    return left;
  }

  private parseTerm(): FilterNode {
    if (this.tryConsumeKeyword("not")) {
      return not(this.parseTerm());
    }
    if (this.current().kind === TokenKind.LParen) {
      this.advance();
      const inner = this.parseOr();
      this.expect(TokenKind.RParen);
      return inner;
    }
    return this.parseComparison();
  }

  private parseComparison(): FilterNode {
    const field = this.parseFieldRef();

    if (this.tryConsumeKeyword("is")) {
      const negated = this.tryConsumeKeyword("not");
      this.expectKeyword("null");
      return comparison(field, negated ? "ne" : "eq", NULL_CONSTANT);
    }

    if (this.tryConsumeKeyword("in")) {
      this.expect(TokenKind.LParen);
      const values = [constant(this.parseLiteral())];
      while (this.current().kind === TokenKind.Comma) {
        this.advance();
        values.push(constant(this.parseLiteral()));
      }
      this.expect(TokenKind.RParen);
      return inList(field, values);
    }

    if (this.tryConsumeKeyword("like")) {
      const pattern = this.parseLiteral();
      const stripped = stripLikeWildcards(pattern);
      return fn("contains", [{ kind: "field", field }, { kind: "constant", value: constant(stripped) }]);
    }

    const op = this.parseOperator();

    if (typeof op === "string" && ["contains", "startsWith", "endsWith"].includes(op)) {
      const value = this.parseLiteral();
      return fn(op as FilterFunction, [{ kind: "field", field }, { kind: "constant", value: constant(value) }]);
    }

    const value = this.parseLiteral();
    return comparison(field, op as "eq" | "ne" | "gt" | "ge" | "lt" | "le", constant(value));
  }

  private parseOperator(): string {
    if (this.current().kind === TokenKind.Operator) {
      const symbol = this.current().text;
      this.advance();
      switch (symbol) {
        case "=": return "eq";
        case "!=":
        case "<>": return "ne";
        case ">": return "gt";
        case ">=": return "ge";
        case "<": return "lt";
        case "<=": return "le";
        default:
          throw this.parseError(`Unknown operator '${symbol}'.`);
      }
    }

    if (this.current().kind === TokenKind.Keyword) {
      const word = this.current().text.toLowerCase();
      if (word === "contains") { this.advance(); return "contains"; }
      if (word === "starts") {
        this.advance();
        this.expectKeyword("with");
        return "startsWith";
      }
      if (word === "ends") {
        this.advance();
        this.expectKeyword("with");
        return "endsWith";
      }
    }

    throw this.parseError(`Expected a comparison operator near '${this.current().text}'.`);
  }

  private parseFieldRef() {
    const path = [this.expectIdentifier()];
    while (this.current().kind === TokenKind.Dot) {
      this.advance();
      path.push(this.expectIdentifier());
    }
    if (path.length > 2) {
      throw new FilterParseError(
        ErrorCodes.ValidationBadFilter,
        "REST filter navigation paths are limited to depth 1.",
      );
    }
    return fieldRef(...path);
  }

  private parseLiteral(): unknown {
    const token = this.current();
    switch (token.kind) {
      case TokenKind.String:
        this.advance();
        return token.text;
      case TokenKind.Number:
        this.advance();
        return token.text.includes(".")
          ? parseFloat(token.text)
          : parseInt(token.text, 10);
      case TokenKind.Keyword:
        if (token.text.toLowerCase() === "true") { this.advance(); return true; }
        if (token.text.toLowerCase() === "false") { this.advance(); return false; }
        if (token.text.toLowerCase() === "null") { this.advance(); return null; }
        // fall through
    }
    throw this.parseError(`Expected a literal value near '${token.text}'.`);
  }

  expect(kind: TokenKind): void {
    if (this.current().kind !== kind) {
      throw this.parseError(`Expected ${TokenKind[kind]} near '${this.current().text}'.`);
    }
    if (kind !== TokenKind.End) this.advance();
  }

  private expectKeyword(keyword: string): void {
    if (!this.tryConsumeKeyword(keyword)) {
      throw this.parseError(`Expected '${keyword}'.`);
    }
  }

  private expectIdentifier(): string {
    if (this.current().kind !== TokenKind.Identifier) {
      throw this.parseError(`Expected a field name near '${this.current().text}'.`);
    }
    const text = this.current().text;
    this.advance();
    return text;
  }

  private tryConsumeKeyword(keyword: string): boolean {
    if (
      this.current().kind === TokenKind.Keyword &&
      this.current().text.toLowerCase() === keyword
    ) {
      this.advance();
      return true;
    }
    return false;
  }

  private current(): Token {
    return this.tokens[this.pos]!;
  }

  private advance(): void {
    this.pos++;
  }

  private parseError(message: string): FilterParseError {
    return new FilterParseError(ErrorCodes.ValidationBadFilter, message);
  }
}

// ---- Tokenizer ----

function tokenize(input: string): Token[] {
  const tokens: Token[] = [];
  let i = 0;

  while (i < input.length) {
    const c = input[i]!;
    if (/\s/.test(c)) { i++; continue; }

    switch (c) {
      case "(": tokens.push({ kind: TokenKind.LParen, text: "(" }); i++; continue;
      case ")": tokens.push({ kind: TokenKind.RParen, text: ")" }); i++; continue;
      case ",": tokens.push({ kind: TokenKind.Comma, text: "," }); i++; continue;
      case ".": tokens.push({ kind: TokenKind.Dot, text: "." }); i++; continue;
    }

    if (c === "'") {
      let str = "";
      i++;
      while (i < input.length) {
        if (input[i] === "'") {
          if (i + 1 < input.length && input[i + 1] === "'") {
            str += "'";
            i += 2;
            continue;
          }
          i++;
          break;
        }
        str += input[i];
        i++;
      }
      tokens.push({ kind: TokenKind.String, text: str });
      continue;
    }

    if ("=!<>".includes(c)) {
      let op = c;
      if (i + 1 < input.length && input[i + 1] === "=") {
        op += "=";
        i += 2;
      } else if (c === "<" && i + 1 < input.length && input[i + 1] === ">") {
        op = "<>";
        i += 2;
      } else {
        i++;
      }
      tokens.push({ kind: TokenKind.Operator, text: op });
      continue;
    }

    if (/\d/.test(c) || (c === "-" && i + 1 < input.length && /\d/.test(input[i + 1]!))) {
      const start = i;
      i++;
      while (i < input.length && /[\d.]/.test(input[i]!)) i++;
      tokens.push({ kind: TokenKind.Number, text: input.slice(start, i) });
      continue;
    }

    if (/[a-zA-Z_]/.test(c)) {
      const start = i;
      while (i < input.length && /[\w]/.test(input[i]!)) i++;
      const word = input.slice(start, i);
      tokens.push({
        kind: KEYWORDS.has(word.toLowerCase()) ? TokenKind.Keyword : TokenKind.Identifier,
        text: word,
      });
      continue;
    }

    throw new FilterParseError(ErrorCodes.ValidationBadFilter, `Unexpected character '${c}'.`);
  }

  tokens.push({ kind: TokenKind.End, text: "" });
  return tokens;
}

function stripLikeWildcards(pattern: unknown): string {
  return String(pattern ?? "").replace(/^%|%$/g, "");
}
