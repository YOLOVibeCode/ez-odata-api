using EzOdata.Core.Query;
using EzOdata.Core.Schema;

namespace EzOdata.Core.Policy;

/// <summary>
/// Prevents cross-table leaks through filters (spec 08 §11 item 2): navigation paths
/// (customer/ssn) and lambda predicates (orders/any(o: o/secret gt 1)) are checked
/// against the policy of the table they actually touch.
/// </summary>
public static class CrossTableFilterValidator
{
    public static void Validate(
        FilterNode filter,
        TableModel rootTable,
        SchemaSnapshot schema,
        Func<TableModel, PolicyDecision> authorizeGet)
    {
        Walk(filter, rootTable, schema, authorizeGet);
    }

    private static void Walk(
        FilterNode node, TableModel table, SchemaSnapshot schema,
        Func<TableModel, PolicyDecision> authorizeGet)
    {
        switch (node)
        {
            case ComparisonNode cmp:
                CheckFieldRef(cmp.Field, table, schema, authorizeGet);
                break;

            case InNode inNode:
                CheckFieldRef(inNode.Field, table, schema, authorizeGet);
                break;

            case FunctionNode fn:
                foreach (var arg in fn.Args.OfType<FieldArg>())
                {
                    CheckFieldRef(arg.Field, table, schema, authorizeGet);
                }

                break;

            case LogicalNode logical:
                foreach (var operand in logical.Operands)
                {
                    Walk(operand, table, schema, authorizeGet);
                }

                break;

            case NotNode not:
                Walk(not.Operand, table, schema, authorizeGet);
                break;

            case LambdaNode lambda:
                var child = FindChildByNavToMany(schema, table, lambda.Navigation)
                    ?? throw new QueryPolicyRewriter.FieldDeniedException(lambda.Navigation);
                EnsureReadable(child, authorizeGet, lambda.Navigation);
                if (lambda.Predicate is not null)
                {
                    Walk(lambda.Predicate, child, schema, authorizeGet);
                }

                break;
        }
    }

    private static void CheckFieldRef(
        FieldRef field, TableModel table, SchemaSnapshot schema,
        Func<TableModel, PolicyDecision> authorizeGet)
    {
        if (!field.IsNavigated) return; // root fields validated by QueryPolicyRewriter

        var current = table;
        for (var i = 0; i < field.Path.Count - 1; i++)
        {
            var nav = field.Path[i];
            var fk = current.ForeignKeys.FirstOrDefault(f => f.NavToOne == nav)
                ?? throw new QueryPolicyRewriter.FieldDeniedException(nav);
            current = schema.FindTable(fk.RefTable)
                ?? throw new QueryPolicyRewriter.FieldDeniedException(nav);
            EnsureReadable(current, authorizeGet, nav);
        }

        var decision = authorizeGet(current);
        var leaf = field.Leaf;
        if (decision.DeniedFields.Contains(leaf)
            || decision.WriteOnlyFields.Contains(leaf)
            || decision.MaskedFields.ContainsKey(leaf))
        {
            throw new QueryPolicyRewriter.FieldDeniedException(leaf);
        }
    }

    private static void EnsureReadable(
        TableModel table, Func<TableModel, PolicyDecision> authorizeGet, string navigation)
    {
        if (!authorizeGet(table).Allowed)
        {
            throw new QueryPolicyRewriter.FieldDeniedException(navigation);
        }
    }

    private static TableModel? FindChildByNavToMany(SchemaSnapshot schema, TableModel parent, string navigation)
    {
        foreach (var candidate in schema.Tables)
        {
            if (candidate.ForeignKeys.Any(fk => fk.RefTable == parent.ExposedName && fk.NavToMany == navigation))
            {
                return candidate;
            }
        }

        return null;
    }
}
