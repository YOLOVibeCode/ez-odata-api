using EzOdata.Connectors.Abstractions;
using EzOdata.Core.Query;
using Microsoft.OData.UriParser;
using Microsoft.OData.UriParser.Aggregation;
using IrAggregation = EzOdata.Core.Query.Aggregation;
using IrApplyClause = EzOdata.Core.Query.ApplyClause;
using IrInNode = EzOdata.Core.Query.InNode;
using IrLambdaNode = EzOdata.Core.Query.LambdaNode;
using OdlInNode = Microsoft.OData.UriParser.InNode;
using OdlLambdaNode = Microsoft.OData.UriParser.LambdaNode;
using ApplyClause2 = Microsoft.OData.UriParser.Aggregation.ApplyClause;

namespace EzOdata.OData;

/// <summary>
/// Translates the official ODataLib URI AST (FilterClause/OrderByClause) into the
/// shared Query IR (spec 02 §5–6). Anything outside the supported grammar fails
/// loudly (OD-9), never silently.
/// </summary>
public static class ODataAstTranslator
{
    public static FilterNode TranslateFilter(FilterClause filter) =>
        TranslateBool(filter.Expression);

    public static IReadOnlyList<OrderByItem> TranslateOrderBy(OrderByClause? orderBy)
    {
        var items = new List<OrderByItem>();
        var current = orderBy;
        while (current is not null)
        {
            if (current.Expression is not SingleValuePropertyAccessNode property)
            {
                throw new NotSupportedQueryException("$orderby supports direct properties only.");
            }

            items.Add(new OrderByItem(property.Property.Name, current.Direction == OrderByDirection.Descending));
            current = current.ThenBy;
        }

        return items;
    }

    /// <summary>$apply transformation → ApplyClause IR; supports the spec 05 §4.5 subset.</summary>
    public static IrApplyClause TranslateApply(ApplyClause2 apply)
    {
        var transformations = apply.Transformations.ToList();
        if (transformations.Count > 2)
        {
            throw new NotSupportedQueryException("$apply supports at most [filter/]groupby or aggregate.");
        }

        // Optional leading filter is applied by the caller via ParseFilter equivalence;
        // here we only translate groupby/aggregate.
        var groupBy = new List<string>();
        var aggregations = new List<Aggregation>();

        foreach (var transformation in transformations)
        {
            switch (transformation)
            {
                case GroupByTransformationNode group:
                    foreach (var property in group.GroupingProperties)
                    {
                        groupBy.Add(property.Name);
                    }

                    if (group.ChildTransformations is AggregateTransformationNode childAggregate)
                    {
                        aggregations.AddRange(TranslateAggregations(childAggregate));
                    }

                    break;

                case AggregateTransformationNode aggregate:
                    aggregations.AddRange(TranslateAggregations(aggregate));
                    break;

                case FilterTransformationNode:
                    // handled separately by the caller (filter applied to the WHERE clause)
                    break;

                default:
                    throw new NotSupportedQueryException(
                        $"$apply transformation '{transformation.Kind}' is not supported (spec 05 §4.5).");
            }
        }

        return new IrApplyClause(groupBy, aggregations);
    }

    private static IEnumerable<IrAggregation> TranslateAggregations(AggregateTransformationNode node)
    {
        foreach (var expression in node.AggregateExpressions.OfType<AggregateExpression>())
        {
            var field = (expression.Expression as SingleValuePropertyAccessNode)?.Property.Name;
            var op = expression.Method switch
            {
                AggregationMethod.Sum => AggregateOp.Sum,
                AggregationMethod.Average => AggregateOp.Average,
                AggregationMethod.Min => AggregateOp.Min,
                AggregationMethod.Max => AggregateOp.Max,
                AggregationMethod.CountDistinct => AggregateOp.CountDistinct,
                AggregationMethod.VirtualPropertyCount => AggregateOp.Count,
                _ => throw new NotSupportedQueryException($"Aggregate method '{expression.Method}' is not supported."),
            };
            yield return new IrAggregation(op, field, expression.Alias);
        }
    }

    /// <summary>Filter inside $apply, if present (applied to WHERE before grouping).</summary>
    public static FilterNode? ExtractApplyFilter(ApplyClause2 apply)
    {
        var filterNode = apply.Transformations.OfType<FilterTransformationNode>().FirstOrDefault();
        return filterNode is null ? null : TranslateFilter(filterNode.FilterClause);
    }

    /// <summary>$expand tree → ExpandNode IR (spec 05 §4.4), with nested options.</summary>
    public static IReadOnlyList<ExpandNode> TranslateExpand(SelectExpandClause? clause, int maxDepth, int maxWidth)
    {
        if (clause is null) return [];

        var expands = clause.SelectedItems.OfType<ExpandedNavigationSelectItem>().ToList();
        if (expands.Count > maxWidth)
        {
            throw new NotSupportedQueryException($"At most {maxWidth} $expand items are allowed per request.");
        }

        return expands.Select(e => TranslateExpandItem(e, 1, maxDepth, maxWidth)).ToList();
    }

    private static ExpandNode TranslateExpandItem(ExpandedNavigationSelectItem item, int depth, int maxDepth, int maxWidth)
    {
        if (depth > maxDepth)
        {
            throw new NotSupportedQueryException($"$expand depth is limited to {maxDepth}.");
        }

        var segment = item.PathToNavigationProperty.LastSegment as NavigationPropertySegment
            ?? throw new NotSupportedQueryException("Unsupported $expand path.");

        var nestedExpands = item.SelectAndExpand?.SelectedItems.OfType<ExpandedNavigationSelectItem>()
            .Select(e => TranslateExpandItem(e, depth + 1, maxDepth, maxWidth)).ToList() ?? [];

        return new ExpandNode
        {
            Navigation = segment.NavigationProperty.Name,
            Filter = item.FilterOption is { } f ? TranslateFilter(f) : null,
            OrderBy = TranslateOrderBy(item.OrderByOption),
            Select = TranslateSelectFields(item.SelectAndExpand),
            Top = checked((int?)item.TopOption),
            Skip = checked((int?)item.SkipOption),
            Expand = nestedExpands,
        };
    }

    private static IReadOnlyList<string>? TranslateSelectFields(SelectExpandClause? clause)
    {
        if (clause is null || clause.AllSelected) return null;

        var fields = new List<string>();
        foreach (var item in clause.SelectedItems.OfType<PathSelectItem>())
        {
            if (item.SelectedPath.FirstSegment is PropertySegment property)
            {
                fields.Add(property.Property.Name);
            }
        }

        return fields.Count > 0 ? fields : null;
    }

    private static FilterNode TranslateBool(SingleValueNode node) => Unwrap(node) switch
    {
        BinaryOperatorNode binary => TranslateBinary(binary),
        UnaryOperatorNode { OperatorKind: UnaryOperatorKind.Not } not =>
            new NotNode(TranslateBool(not.Operand)),
        SingleValueFunctionCallNode fn => TranslateBooleanFunction(fn),
        AnyNode any => TranslateLambda(any, LambdaKind.Any),
        AllNode all => TranslateLambda(all, LambdaKind.All),
        OdlInNode inNode => TranslateIn(inNode),
        _ => throw new NotSupportedQueryException(
            $"Unsupported filter expression '{node.GetType().Name}'."),
    };

    private static FilterNode TranslateBinary(BinaryOperatorNode binary)
    {
        switch (binary.OperatorKind)
        {
            case BinaryOperatorKind.And:
            case BinaryOperatorKind.Or:
                return new LogicalNode(
                    binary.OperatorKind == BinaryOperatorKind.And ? LogicalOp.And : LogicalOp.Or,
                    [TranslateBool(binary.Left), TranslateBool(binary.Right)]);

            case BinaryOperatorKind.Equal:
            case BinaryOperatorKind.NotEqual:
            case BinaryOperatorKind.GreaterThan:
            case BinaryOperatorKind.GreaterThanOrEqual:
            case BinaryOperatorKind.LessThan:
            case BinaryOperatorKind.LessThanOrEqual:
                return TranslateComparison(binary);

            default:
                throw new NotSupportedQueryException($"Operator '{binary.OperatorKind}' is not supported in $filter.");
        }
    }

    private static FilterNode TranslateComparison(BinaryOperatorNode binary)
    {
        var op = binary.OperatorKind switch
        {
            BinaryOperatorKind.Equal => ComparisonOp.Eq,
            BinaryOperatorKind.NotEqual => ComparisonOp.Ne,
            BinaryOperatorKind.GreaterThan => ComparisonOp.Gt,
            BinaryOperatorKind.GreaterThanOrEqual => ComparisonOp.Ge,
            BinaryOperatorKind.LessThan => ComparisonOp.Lt,
            BinaryOperatorKind.LessThanOrEqual => ComparisonOp.Le,
            _ => throw new NotSupportedQueryException("Unexpected comparison kind."),
        };

        var left = Unwrap(binary.Left);
        var right = Unwrap(binary.Right);

        // property <op> constant
        if (TryFieldPath(left, out var field) && right is ConstantNode constant)
        {
            return new ComparisonNode(field, op, ToConstant(constant));
        }

        // function(...) <op> constant — e.g. year(created_at) eq 2026, length(name) gt 5
        if (left is SingleValueFunctionCallNode fn && right is ConstantNode fnComparand)
        {
            var (function, args) = TranslateFunctionCall(fn);
            return new FunctionNode(function, args, op, ToConstant(fnComparand));
        }

        // constant <op> property (flipped)
        if (left is ConstantNode flipped && TryFieldPath(right, out var flippedField))
        {
            return new ComparisonNode(flippedField, Flip(op), ToConstant(flipped));
        }

        throw new NotSupportedQueryException(
            "Comparisons must be between a property (or supported function) and a literal.");
    }

    private static FilterNode TranslateBooleanFunction(SingleValueFunctionCallNode fn)
    {
        var (function, args) = TranslateFunctionCall(fn);
        if (function is not (FilterFunction.Contains or FilterFunction.StartsWith or FilterFunction.EndsWith))
        {
            throw new NotSupportedQueryException(
                $"Function '{fn.Name}' must be used in a comparison (e.g. {fn.Name}(...) eq value).");
        }

        return new FunctionNode(function, args);
    }

    private static (FilterFunction Function, IReadOnlyList<FilterArg> Args) TranslateFunctionCall(SingleValueFunctionCallNode fn)
    {
        var function = fn.Name.ToLowerInvariant() switch
        {
            "contains" => FilterFunction.Contains,
            "startswith" => FilterFunction.StartsWith,
            "endswith" => FilterFunction.EndsWith,
            "tolower" => FilterFunction.ToLower,
            "toupper" => FilterFunction.ToUpper,
            "trim" => FilterFunction.Trim,
            "length" => FilterFunction.Length,
            "indexof" => FilterFunction.IndexOf,
            "substring" => FilterFunction.Substring,
            "concat" => FilterFunction.Concat,
            "year" => FilterFunction.Year,
            "month" => FilterFunction.Month,
            "day" => FilterFunction.Day,
            "hour" => FilterFunction.Hour,
            "minute" => FilterFunction.Minute,
            "second" => FilterFunction.Second,
            "date" => FilterFunction.Date,
            "time" => FilterFunction.Time,
            "now" => FilterFunction.Now,
            "round" => FilterFunction.Round,
            "floor" => FilterFunction.Floor,
            "ceiling" => FilterFunction.Ceiling,
            _ => throw new NotSupportedQueryException($"Function '{fn.Name}' is not supported (spec 05 §4.3)."),
        };

        var args = new List<FilterArg>();
        foreach (var parameter in fn.Parameters)
        {
            var unwrapped = Unwrap((SingleValueNode)parameter);
            if (TryFieldPath(unwrapped, out var field))
            {
                args.Add(new FieldArg(field));
            }
            else if (unwrapped is ConstantNode constant)
            {
                args.Add(new ConstantArg(ToConstant(constant)));
            }
            else if (unwrapped is SingleValueFunctionCallNode nested)
            {
                throw new NotSupportedQueryException(
                    $"Nested function calls are not supported ('{nested.Name}' inside '{fn.Name}').");
            }
            else
            {
                throw new NotSupportedQueryException($"Unsupported argument in '{fn.Name}'.");
            }
        }

        return (function, args);
    }

    private static FilterNode TranslateLambda(OdlLambdaNode lambda, LambdaKind kind)
    {
        if (lambda.Source is not CollectionNavigationNode navigation)
        {
            throw new NotSupportedQueryException("any/all is supported on first-level navigations only.");
        }

        var predicate = lambda.Body is ConstantNode { Value: true }
            ? null
            : TranslateBool(lambda.Body);

        return new IrLambdaNode(navigation.NavigationProperty.Name, kind, predicate);
    }

    private static FilterNode TranslateIn(OdlInNode inNode)
    {
        if (inNode.Left is not SingleValuePropertyAccessNode property)
        {
            throw new NotSupportedQueryException("'in' requires a property on the left side.");
        }

        if (inNode.Right is not CollectionConstantNode collection)
        {
            throw new NotSupportedQueryException("'in' requires a literal list on the right side.");
        }

        var values = collection.Collection.Select(c => ToConstant(c)).ToList();
        return new IrInNode(new FieldRef(property.Property.Name), values);
    }

    private static bool TryFieldPath(SingleValueNode node, out FieldRef field)
    {
        var path = new List<string>();
        var current = node;
        while (true)
        {
            current = Unwrap(current);
            if (current is SingleValuePropertyAccessNode property)
            {
                path.Insert(0, property.Property.Name);
                current = property.Source;
            }
            else if (current is SingleNavigationNode navigation)
            {
                path.Insert(0, navigation.NavigationProperty.Name);
                current = navigation.Source;
            }
            else if (current is ResourceRangeVariableReferenceNode)
            {
                break;
            }
            else
            {
                field = null!;
                return false; // unsupported source → not a plain field path
            }
        }

        if (path.Count == 0)
        {
            field = null!;
            return false;
        }

        field = new FieldRef(path);
        return true;
    }

    private static SingleValueNode Unwrap(SingleValueNode node) =>
        node is ConvertNode convert ? Unwrap(convert.Source) : node;

    private static ConstantValue ToConstant(ConstantNode constant) =>
        new(NormalizeValue(constant.Value));

    /// <summary>ODL literal types → provider-friendly CLR types.</summary>
    internal static object? NormalizeValue(object? value) => value switch
    {
        null => null,
        Microsoft.OData.Edm.Date date => new DateTime(date.Year, date.Month, date.Day, 0, 0, 0, DateTimeKind.Unspecified),
        Microsoft.OData.Edm.TimeOfDay time => new TimeSpan(0, time.Hours, time.Minutes, time.Seconds),
        _ => value,
    };

    private static ComparisonOp Flip(ComparisonOp op) => op switch
    {
        ComparisonOp.Gt => ComparisonOp.Lt,
        ComparisonOp.Ge => ComparisonOp.Le,
        ComparisonOp.Lt => ComparisonOp.Gt,
        ComparisonOp.Le => ComparisonOp.Ge,
        _ => op,
    };
}
