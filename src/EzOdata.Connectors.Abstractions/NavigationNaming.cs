namespace EzOdata.Connectors.Abstractions;

/// <summary>
/// Deterministic navigation property naming shared by all introspectors (spec 04 §5.3).
/// </summary>
public static class NavigationNaming
{
    /// <summary>
    /// Many-to-one name: FK column stripped of an _id/Id suffix when unambiguous,
    /// else the referenced table name, else ref_{fkName}.
    /// </summary>
    public static string ToOneName(
        IReadOnlyList<string> fkColumns, string refTable, string fkName,
        ICollection<string> takenNames)
    {
        string? candidate = null;
        if (fkColumns.Count == 1)
        {
            var column = fkColumns[0];
            if (column.EndsWith("_id", StringComparison.OrdinalIgnoreCase) && column.Length > 3)
            {
                candidate = column.Substring(0, column.Length - 3);
            }
            else if (column.EndsWith("Id", StringComparison.Ordinal) && column.Length > 2)
            {
                candidate = column.Substring(0, column.Length - 2);
            }
        }

        candidate ??= refTable;
        if (takenNames.Contains(candidate)) candidate = refTable;
        if (takenNames.Contains(candidate)) candidate = $"ref_{fkName}";

        takenNames.Add(candidate);
        return candidate;
    }

    /// <summary>One-to-many name: child table exposed name; on collision append _{fkName}.</summary>
    public static string ToManyName(string childTable, string fkName, ICollection<string> takenNames)
    {
        var candidate = childTable;
        if (takenNames.Contains(candidate)) candidate = $"{childTable}_{fkName}";

        takenNames.Add(candidate);
        return candidate;
    }
}
