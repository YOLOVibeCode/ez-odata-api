/**
 * Case-insensitive glob matching for table/field patterns: `*` and `?` only
 * (port of src/EzOdata.Core/Text/Glob.cs, spec 03 §2.4).
 */

const REGEX_META = /[.*+?^${}()|[\]\\]/g;

function escapeRegex(value: string): string {
  return value.replace(REGEX_META, "\\$&");
}

export function isMatch(value: string, pattern: string): boolean {
  if (pattern === "*") return true;

  const body = escapeRegex(pattern).replace(/\\\*/g, ".*").replace(/\\\?/g, ".");
  return new RegExp(`^${body}$`, "i").test(value);
}

export function matchesAny(value: string, patterns: readonly string[]): boolean {
  return patterns.some((p) => isMatch(value, p));
}
