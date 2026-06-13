using System.Collections.Concurrent;
using EzOdata.Core.Schema;
using Microsoft.OData.Edm;

namespace EzOdata.OData;

/// <summary>
/// Builds an immutable <see cref="IEdmModel"/> from a schema snapshot (spec 05 §3).
/// Models are cached by (service, schemaVersion); refresh swaps atomically.
/// </summary>
public sealed class EdmModelFactory
{
    private readonly ConcurrentDictionary<string, IEdmModel> _cache = new(StringComparer.Ordinal);

    public IEdmModel GetOrBuild(string serviceName, string schemaVersion, SchemaSnapshot snapshot)
    {
        var key = $"{serviceName}:{schemaVersion}";
        return _cache.GetOrAdd(key, _ => Build(serviceName, snapshot));
    }

    public static IEdmModel Build(string serviceName, SchemaSnapshot snapshot)
    {
        var model = new EdmModel();
        var ns = "EzOdata." + Sanitize(serviceName);
        var container = new EdmEntityContainer(ns, "Container");
        model.AddElement(container);

        // Pass 1: entity types with structural properties + keys
        var types = new Dictionary<string, EdmEntityType>(StringComparer.Ordinal);
        var sets = new Dictionary<string, EdmEntitySet>(StringComparer.Ordinal);

        foreach (var table in snapshot.Tables)
        {
            var typeName = MakeTypeName(table.ExposedName, snapshot);
            var entityType = new EdmEntityType(ns, typeName);

            var keyProps = new List<IEdmStructuralProperty>();
            foreach (var column in table.Columns)
            {
                var typeRef = MapType(column);
                if (typeRef is null) continue; // unmappable; excluded entirely

                var property = entityType.AddStructuralProperty(column.ExposedName, typeRef);
                if (table.PrimaryKey.Contains(column.ExposedName)) keyProps.Add(property);
            }

            if (keyProps.Count > 0) entityType.AddKeys(keyProps);

            model.AddElement(entityType);
            types[table.ExposedName] = entityType;
            sets[table.ExposedName] = container.AddEntitySet(table.ExposedName, entityType);
        }

        // Pass 2: navigation properties + bindings
        foreach (var table in snapshot.Tables)
        {
            foreach (var fk in table.ForeignKeys)
            {
                if (!types.TryGetValue(table.ExposedName, out var childType)) continue;
                if (!types.TryGetValue(fk.RefTable, out var parentType)) continue;

                var dependentProps = fk.Columns
                    .Select(c => childType.FindProperty(c) as IEdmStructuralProperty)
                    .Where(p => p is not null).Cast<IEdmStructuralProperty>().ToList();
                var principalProps = fk.RefColumns
                    .Select(c => parentType.FindProperty(c) as IEdmStructuralProperty)
                    .Where(p => p is not null).Cast<IEdmStructuralProperty>().ToList();

                var navToOne = childType.AddBidirectionalNavigation(
                    new EdmNavigationPropertyInfo
                    {
                        Name = fk.NavToOne,
                        Target = parentType,
                        TargetMultiplicity = dependentProps.All(p => !p.Type.IsNullable)
                            ? EdmMultiplicity.One
                            : EdmMultiplicity.ZeroOrOne,
                        DependentProperties = dependentProps,
                        PrincipalProperties = principalProps,
                    },
                    new EdmNavigationPropertyInfo
                    {
                        Name = fk.NavToMany,
                        Target = childType,
                        TargetMultiplicity = EdmMultiplicity.Many,
                    });

                sets[table.ExposedName].AddNavigationTarget(navToOne, sets[fk.RefTable]);
                var partner = parentType.FindProperty(fk.NavToMany) as IEdmNavigationProperty;
                if (partner is not null)
                {
                    sets[fk.RefTable].AddNavigationTarget(partner, sets[table.ExposedName]);
                }
            }
        }

        return model;
    }

    private static IEdmTypeReference? MapType(ColumnModel column)
    {
        var nullable = column.Nullable;
        if (column.EdmType.StartsWith("Collection(", StringComparison.Ordinal))
        {
            var element = column.EdmType.Substring("Collection(".Length).TrimEnd(')');
            var elementKind = PrimitiveKind(element);
            return elementKind is null
                ? EdmCoreModel.Instance.GetString(true)
                : EdmCoreModel.GetCollection(EdmCoreModel.Instance.GetPrimitive(elementKind.Value, true));
        }

        if (column.EdmType == "Edm.Untyped")
        {
            return EdmCoreModel.Instance.GetUntyped();
        }

        var kind = PrimitiveKind(column.EdmType);
        if (kind is null) return EdmCoreModel.Instance.GetString(nullable);

        return kind switch
        {
            EdmPrimitiveTypeKind.String when column.MaxLength is { } max =>
                EdmCoreModel.Instance.GetString(false, max, true, nullable),
            EdmPrimitiveTypeKind.Decimal =>
                EdmCoreModel.Instance.GetDecimal(column.Precision, column.Scale, nullable),
            _ => EdmCoreModel.Instance.GetPrimitive(kind.Value, nullable),
        };
    }

    private static EdmPrimitiveTypeKind? PrimitiveKind(string edmType) => edmType switch
    {
        "Edm.Int16" => EdmPrimitiveTypeKind.Int16,
        "Edm.Int32" => EdmPrimitiveTypeKind.Int32,
        "Edm.Int64" => EdmPrimitiveTypeKind.Int64,
        "Edm.Decimal" => EdmPrimitiveTypeKind.Decimal,
        "Edm.Double" => EdmPrimitiveTypeKind.Double,
        "Edm.Single" => EdmPrimitiveTypeKind.Single,
        "Edm.Boolean" => EdmPrimitiveTypeKind.Boolean,
        "Edm.String" => EdmPrimitiveTypeKind.String,
        "Edm.Guid" => EdmPrimitiveTypeKind.Guid,
        "Edm.DateTimeOffset" => EdmPrimitiveTypeKind.DateTimeOffset,
        "Edm.Date" => EdmPrimitiveTypeKind.Date,
        "Edm.TimeOfDay" => EdmPrimitiveTypeKind.TimeOfDay,
        "Edm.Duration" => EdmPrimitiveTypeKind.Duration,
        "Edm.Binary" => EdmPrimitiveTypeKind.Binary,
        _ => null,
    };

    /// <summary>Entity type name = exposed table name; "Type" appended only on collision (spec 05 §3.2).</summary>
    private static string MakeTypeName(string exposedName, SchemaSnapshot snapshot) => exposedName;

    private static string Sanitize(string serviceName)
    {
        var chars = serviceName.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray();
        var result = new string(chars);
        return char.IsLetter(result[0]) ? result : "s_" + result;
    }
}
