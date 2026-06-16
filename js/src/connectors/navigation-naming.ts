/**
 * Deterministic navigation property naming shared by all introspectors
 * (port of NavigationNaming.cs, spec 04 §5.3).
 */

/**
 * Many-to-one name: FK column stripped of an _id/Id suffix when unambiguous,
 * else the referenced table name, else ref_{fkName}. Mutates takenNames.
 */
export function toOneName(
  fkColumns: readonly string[],
  refTable: string,
  fkName: string,
  takenNames: Set<string>,
): string {
  let candidate: string | undefined;
  if (fkColumns.length === 1) {
    const col = fkColumns[0]!;
    if (col.toLowerCase().endsWith("_id") && col.length > 3) {
      candidate = col.slice(0, -3);
    } else if (col.endsWith("Id") && col.length > 2) {
      candidate = col.slice(0, -2);
    }
  }
  candidate ??= refTable;
  if (takenNames.has(candidate)) candidate = refTable;
  if (takenNames.has(candidate)) candidate = `ref_${fkName}`;
  takenNames.add(candidate);
  return candidate;
}

/** One-to-many name: child table exposed name; on collision append _{fkName}. Mutates takenNames. */
export function toManyName(childTable: string, fkName: string, takenNames: Set<string>): string {
  let candidate = childTable;
  if (takenNames.has(candidate)) candidate = `${childTable}_${fkName}`;
  takenNames.add(candidate);
  return candidate;
}
