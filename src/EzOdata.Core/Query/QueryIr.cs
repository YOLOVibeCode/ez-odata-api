namespace EzOdata.Core.Query;

/// <summary>
/// The shared Query IR (spec 02 §6): OData, REST, and MCP all parse into these types;
/// the policy engine rewrites them; connectors compile them to parameterized SQL.
/// </summary>
public sealed record QueryRequest
{
    public required string ServiceName { get; init; }
    public required string Table { get; init; }
    public FilterNode? Filter { get; init; }
    public IReadOnlyList<OrderByItem> OrderBy { get; init; } = [];

    /// <summary>null = all permitted fields.</summary>
    public IReadOnlyList<string>? Select { get; init; }

    public IReadOnlyList<ExpandNode> Expand { get; init; } = [];
    public int? Top { get; init; }
    public int? Skip { get; init; }
    public bool Count { get; init; }
    public KeysetCursor? Cursor { get; init; }

    /// <summary>$apply groupby/aggregate transformation (spec 05 §4.5); null = normal query.</summary>
    public ApplyClause? Apply { get; init; }

    /// <summary>$search term compiled to OR-ed contains across searchable columns (spec 05 §4.6).</summary>
    public string? Search { get; init; }
}

/// <summary>
/// Supported $apply subset (spec 05 §4.5): an optional filter, then groupby with
/// aggregates, or a bare aggregate.
/// </summary>
public sealed record ApplyClause(
    IReadOnlyList<string> GroupBy,
    IReadOnlyList<Aggregation> Aggregations);

public sealed record Aggregation(AggregateOp Op, string? Field, string Alias);

public enum AggregateOp { Sum, Average, Min, Max, CountDistinct, Count }

public sealed record OrderByItem(string Field, bool Descending);

public sealed record ExpandNode
{
    public required string Navigation { get; init; }
    public FilterNode? Filter { get; init; }
    public IReadOnlyList<string>? Select { get; init; }
    public IReadOnlyList<OrderByItem> OrderBy { get; init; } = [];
    public IReadOnlyList<ExpandNode> Expand { get; init; } = [];
    public int? Top { get; init; }
    public int? Skip { get; init; }
}

/// <summary>Opaque keyset pagination state (spec 05 §4.2), decoded from a signed skiptoken.</summary>
public sealed record KeysetCursor(IReadOnlyList<object?> LastOrderByValues, IReadOnlyList<object?> LastKeyValues);

// ---- Filter expression tree ----

public abstract record FilterNode;

public sealed record ComparisonNode(FieldRef Field, ComparisonOp Op, ConstantValue Value) : FilterNode;

public sealed record LogicalNode(LogicalOp Op, IReadOnlyList<FilterNode> Operands) : FilterNode;

public sealed record NotNode(FilterNode Operand) : FilterNode;

public sealed record InNode(FieldRef Field, IReadOnlyList<ConstantValue> Values) : FilterNode;

/// <summary>String/date/math function comparisons, e.g. contains(name,'x'), year(created) eq 2026.</summary>
public sealed record FunctionNode(FilterFunction Function, IReadOnlyList<FilterArg> Args, ComparisonOp? Op = null, ConstantValue? Comparand = null) : FilterNode;

/// <summary>any/all over a to-many navigation (spec 05 §4.3); Predicate is null for bare any().</summary>
public sealed record LambdaNode(string Navigation, LambdaKind Kind, FilterNode? Predicate) : FilterNode;

/// <summary>A field path: bare column, or to-one navigation path of depth ≤ 2 (e.g. customer/country).</summary>
public sealed record FieldRef(IReadOnlyList<string> Path)
{
    public FieldRef(string field) : this([field]) { }

    public string Leaf => Path[Path.Count - 1];
    public bool IsNavigated => Path.Count > 1;
    public override string ToString() => string.Join("/", Path);

    public bool Equals(FieldRef? other) =>
        other is not null && Path.SequenceEqual(other.Path, StringComparer.Ordinal);

    public override int GetHashCode() => ToString().GetHashCode();
}

public abstract record FilterArg;

public sealed record FieldArg(FieldRef Field) : FilterArg;

public sealed record ConstantArg(ConstantValue Value) : FilterArg;

/// <summary>Typed constant; Value is null for the null literal.</summary>
public sealed record ConstantValue(object? Value)
{
    public static readonly ConstantValue Null = new((object?)null);
}

public enum ComparisonOp { Eq, Ne, Gt, Ge, Lt, Le }

public enum LogicalOp { And, Or }

public enum LambdaKind { Any, All }

public enum FilterFunction
{
    Contains, StartsWith, EndsWith,
    ToLower, ToUpper, Trim, Length, IndexOf, Substring, Concat,
    Year, Month, Day, Hour, Minute, Second, Date, Time, Now,
    Round, Floor, Ceiling,
    Add, Sub, Mul, Div, Mod,
}
