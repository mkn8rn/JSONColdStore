using System.Globalization;
using System.Reflection;
using Microsoft.EntityFrameworkCore.Metadata;

namespace EFC.JSONColdStore.Infrastructure;

internal sealed record JsonColdStoreModelDescriptor(IReadOnlyList<JsonColdStoreEntityDescriptor> Entities)
{
    internal static JsonColdStoreModelDescriptor Create(IModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        var entities = model.GetEntityTypes()
            .OrderBy(entityType => entityType.Name, StringComparer.Ordinal)
            .Select(CreateEntityDescriptor)
            .ToArray();

        return new JsonColdStoreModelDescriptor(entities);
    }

    internal JsonColdStoreEntityDescriptor FindEntity(Type clrType)
    {
        ArgumentNullException.ThrowIfNull(clrType);

        return Entities.FirstOrDefault(entity => entity.ClrType == clrType)
            ?? throw new InvalidOperationException(
                $"The entity type '{clrType.FullName ?? clrType.Name}' is not part of the JSONColdStore model.");
    }

    private static JsonColdStoreEntityDescriptor CreateEntityDescriptor(IEntityType entityType)
    {
        var primaryKey = entityType.FindPrimaryKey()
            ?? throw new NotSupportedException(
                $"JSONColdStore entity '{entityType.Name}' must define a primary key.");

        if (primaryKey.Properties.Count != 1)
        {
            throw new NotSupportedException(
                $"JSONColdStore entity '{entityType.Name}' must use one primary key property.");
        }

        var keyProperty = primaryKey.Properties[0];
        var keyPropertyInfo = keyProperty.PropertyInfo
            ?? throw new NotSupportedException(
                $"JSONColdStore entity '{entityType.Name}' must use a CLR property-backed primary key.");
        var indexes = entityType.GetIndexes()
            .Select(index =>
            {
                var properties = index.Properties
                    .Select(property => property.PropertyInfo
                        ?? throw new NotSupportedException(
                            $"JSONColdStore index on entity '{entityType.Name}' must use CLR property-backed members."))
                    .ToArray();

                return new JsonColdStoreIndexDescriptor(
                    index.Properties.Select(property => property.Name).ToArray(),
                    properties,
                    index.IsUnique);
            })
            .OrderBy(index => string.Join("|", index.PropertyNames), StringComparer.Ordinal)
            .ToArray();

        return new JsonColdStoreEntityDescriptor(
            GetStorageEntityName(entityType),
            entityType.ClrType,
            new JsonColdStoreKeyDescriptor(keyProperty.Name, keyProperty.ClrType),
            keyPropertyInfo,
            indexes);
    }

    private static string GetStorageEntityName(IEntityType entityType) =>
        !string.IsNullOrWhiteSpace(entityType.ClrType.FullName)
            ? entityType.ClrType.FullName
            : entityType.Name;
}

internal sealed record JsonColdStoreEntityDescriptor(
    string EntityName,
    Type ClrType,
    JsonColdStoreKeyDescriptor Key,
    PropertyInfo KeyProperty,
    IReadOnlyList<JsonColdStoreIndexDescriptor> Indexes)
{
    internal string CreateRecordId(object? keyValue) => Key.CreateRecordId(keyValue);

    internal string CreateRecordIdFromEntity(object entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (!ClrType.IsInstanceOfType(entity))
        {
            throw new InvalidOperationException(
                $"The entity instance is not assignable to '{ClrType.FullName ?? ClrType.Name}'.");
        }

        return CreateRecordId(KeyProperty.GetValue(entity));
    }

    internal JsonColdStoreIndexDescriptor FindSinglePropertyIndex(string propertyName)
    {
        if (string.IsNullOrWhiteSpace(propertyName))
            throw new ArgumentException("An index property name is required.", nameof(propertyName));

        return Indexes.FirstOrDefault(index =>
                index.PropertyNames.Length == 1
                && string.Equals(index.PropertyNames[0], propertyName, StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                $"The entity type '{ClrType.FullName ?? ClrType.Name}' does not declare a JSONColdStore index on '{propertyName}'.");
    }
}

internal sealed record JsonColdStoreKeyDescriptor(string PropertyName, Type ClrType)
{
    internal string CreateRecordId(object? keyValue)
    {
        if (keyValue is null)
            throw new InvalidOperationException(
                $"The JSONColdStore primary key '{PropertyName}' cannot be null.");

        var value = Convert.ToString(keyValue, CultureInfo.InvariantCulture);
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException(
                $"The JSONColdStore primary key '{PropertyName}' cannot be empty.");

        return value;
    }
}

internal sealed record JsonColdStoreIndexDescriptor(
    string[] PropertyNames,
    PropertyInfo[] Properties,
    bool IsUnique)
{
    internal string StorageName => string.Join("__", PropertyNames);

    internal string CreateIndexKeyFromEntity(object entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return CreateIndexKey(Properties.Select(property => property.GetValue(entity)));
    }

    internal string CreateIndexKeyFromValues(params object?[] values)
    {
        if (values.Length != PropertyNames.Length)
        {
            throw new ArgumentException(
                "Index value count must match the index property count.",
                nameof(values));
        }

        return CreateIndexKey(values);
    }

    private static string CreateIndexKey(IEnumerable<object?> values) =>
        string.Join(
            "\u001F",
            values.Select(value => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "<null>"));
}
