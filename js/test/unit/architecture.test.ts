import { readdirSync, readFileSync, statSync } from "node:fs";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import ts from "typescript";
import { describe, expect, it } from "vitest";

/**
 * ISP guard (the TS analog of the .NET LayeringTests interface-size rule, spec
 * 02 §3.1): a behavioral interface - one with at least one method-shaped member
 * - must stay small (<= MAX_METHODS). Pure-data interfaces (DTOs/records) are
 * exempt, mirroring the .NET rule that counts methods, not property getters.
 */
const MAX_METHODS = 4;
const SRC_DIR = fileURLToPath(new URL("../../src", import.meta.url));

function tsFiles(dir: string): string[] {
  const out: string[] = [];
  for (const entry of readdirSync(dir)) {
    const full = join(dir, entry);
    if (statSync(full).isDirectory()) {
      out.push(...tsFiles(full));
    } else if (entry.endsWith(".ts")) {
      out.push(full);
    }
  }
  return out;
}

function isMethodMember(member: ts.TypeElement): boolean {
  if (ts.isMethodSignature(member)) return true;
  // A property whose type is a function (e.g. `parse: (x: string) => T`) is behavioral too.
  return (
    ts.isPropertySignature(member) &&
    member.type !== undefined &&
    (ts.isFunctionTypeNode(member.type) || ts.isConstructorTypeNode(member.type))
  );
}

interface Violation {
  file: string;
  name: string;
  methodCount: number;
}

function collectViolations(): Violation[] {
  const violations: Violation[] = [];
  for (const file of tsFiles(SRC_DIR)) {
    const source = ts.createSourceFile(file, readFileSync(file, "utf8"), ts.ScriptTarget.ES2022, true);
    source.forEachChild((node) => {
      if (!ts.isInterfaceDeclaration(node)) return;
      const methodCount = node.members.filter(isMethodMember).length;
      if (methodCount > 0 && methodCount > MAX_METHODS) {
        violations.push({ file: file.slice(SRC_DIR.length + 1), name: node.name.text, methodCount });
      }
    });
  }
  return violations;
}

describe("Architecture: Interface Segregation", () => {
  it("keeps behavioral interfaces at or below the method budget", () => {
    const violations = collectViolations();
    expect(violations, JSON.stringify(violations, null, 2)).toEqual([]);
  });

  it("actually inspects the source tree (guard is wired up)", () => {
    expect(tsFiles(SRC_DIR).length).toBeGreaterThan(5);
  });
});
