using System.Globalization;
using System.Reflection;
using Microsoft.EntityFrameworkCore.Metadata;

namespace JSONColdStore.Infrastructure;

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

        return Entities.FirstOrDefault(entity => !entity.IsSharedType && entity.ClrType == clrType)
            ?? throw new InvalidOperationException(
                $"The entity type '{clrType.FullName ?? clrType.Name}' is not part of the JSONColdStore model.");
    }

    internal JsonColdStoreEntityDescriptor FindEntity(IEntityType entityType)
    {
        ArgumentNullException.ThrowIfNull(entityType);

        var entityName = GetStorageEntityName(entityType);
        return Entities.FirstOrDefault(entity => string.Equals(entity.EntityName, entityName, StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                $"The entity type '{entityType.Name}' is not part of the JSONColdStore model.");
    }

    private static JsonColdStoreEntityDescriptor CreateEntityDescriptor(IEntityType entityType)
    {
        var primaryKey = entityType.FindPrimaryKey()
            ?? throw new NotSupportedException(
                $"JSONColdStore entity '{entityType.Name}' must define a primary key.");

        var propertiesByName = entityType.GetProperties()
            .OrderBy(property => property.Name, StringComparer.Ordinal)
            .ToDictionary(
                property => property.Name,
                property => CreatePropertyDescriptor(entityType, property, "property"),
                StringComparer.Ordinal);
        var properties = propertiesByName.Values.ToArray();
        var keyProperties = primaryKey.Properties
            .Select(property => propertiesByName[property.Name])
            .ToArray();
        var indexes = entityType.GetIndexes()
            .Select(index =>
            {
                var properties = index.Properties
                    .Select(property => propertiesByName[property.Name])
                    .ToArray();

                return new JsonColdStoreIndexDescriptor(
                    index.Properties.Select(property => property.Name).ToArray(),
                    properties,
                    index.IsUnique);
            })
            .OrderBy(index => string.Join("|", index.PropertyNames), StringComparer.Ordinal)
            .ToArray();
        var referenceNavigations = entityType.GetNavigations()
            .Where(navigation => !navigation.IsCollection && navigation.IsOnDependent)
            .Select(navigation => CreateReferenceNavigationDescriptor(navigation, propertiesByName))
            .Where(navigation => navigation is not null)
            .Cast<JsonColdStoreReferenceNavigationDescriptor>()
            .OrderBy(navigation => navigation.Name, StringComparer.Ordinal)
            .ToArray();

        return new JsonColdStoreEntityDescriptor(
            GetStorageEntityName(entityType),
            entityType.ClrType,
            entityType.HasSharedClrType,
            properties,
            new JsonColdStoreKeyDescriptor(keyProperties),
            indexes,
            referenceNavigations);
    }

    private static JsonColdStorePropertyDescriptor CreatePropertyDescriptor(
        IEntityType entityType,
        IProperty property,
        string usage)
    {
        var propertyInfo = property.PropertyInfo;
        if (propertyInfo?.GetIndexParameters().Length > 0)
            propertyInfo = null;

        if (propertyInfo is null && !entityType.HasSharedClrType)
        {
            throw new NotSupportedException(
                $"JSONColdStore {usage} on entity '{entityType.Name}' must use CLR property-backed members.");
        }

        return new JsonColdStorePropertyDescriptor(
            property.Name,
            property.ClrType,
            propertyInfo);
    }

    private static string GetStorageEntityName(IEntityType entityType) =>
        !entityType.HasSharedClrType && !string.IsNullOrWhiteSpace(entityType.ClrType.FullName)
            ? entityType.ClrType.FullName
            : entityType.Name;

    private static JsonColdStoreReferenceNavigationDescriptor? CreateReferenceNavigationDescriptor(
        INavigation navigation,
        IReadOnlyDictionary<string, JsonColdStorePropertyDescriptor> propertiesByName)
    {
        var propertyInfo = navigation.PropertyInfo;
        if (propertyInfo is null || propertyInfo.GetIndexParameters().Length > 0)
            return null;

        var foreignKeyProperties = navigation.ForeignKey.Properties
            .Select(property => propertiesByName[property.Name])
            .ToArray();
        var principalKeyPropertyNames = navigation.ForeignKey.PrincipalKey.Properties
            .Select(property => property.Name)
            .ToArray();

        return new JsonColdStoreReferenceNavigationDescriptor(
            navigation.Name,
            navigation.TargetEntityType.ClrType,
            propertyInfo,
            foreignKeyProperties,
            principalKeyPropertyNames);
    }
}

internal sealed record JsonColdStoreEntityDescriptor(
    string EntityName,
    Type ClrType,
    bool IsSharedType,
    IReadOnlyList<JsonColdStorePropertyDescriptor> Properties,
    JsonColdStoreKeyDescriptor Key,
    IReadOnlyList<JsonColdStoreIndexDescriptor> Indexes,
    IReadOnlyList<JsonColdStoreReferenceNavigationDescriptor> ReferenceNavigations)
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

        return Key.CreateRecordIdFromEntity(entity);
    }

    internal IReadOnlyDictionary<string, object?> CreateRecordPayload(object entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (!ClrType.IsInstanceOfType(entity))
        {
            throw new InvalidOperationException(
                $"The entity instance is not assignable to '{ClrType.FullName ?? ClrType.Name}'.");
        }

        return Properties.ToDictionary(
            property => property.Name,
            property => property.GetValue(entity),
            StringComparer.Ordinal);
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

    internal JsonColdStoreReferenceNavigationDescriptor? FindReferenceNavigation(string navigationName) =>
        ReferenceNavigations.FirstOrDefault(navigation =>
            string.Equals(navigation.Name, navigationName, StringComparison.Ordinal));
}

internal sealed record JsonColdStorePropertyDescriptor(
    string Name,
    Type ClrType,
    PropertyInfo? PropertyInfo)
{
    internal object? GetValue(object entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (PropertyInfo is not null)
            return PropertyInfo.GetValue(entity);

        if (entity is IReadOnlyDictionary<string, object?> readOnlyDictionary
            && readOnlyDictionary.TryGetValue(Name, out var readOnlyValue))
        {
            return readOnlyValue;
        }

        if (entity is IDictionary<string, object?> dictionary
            && dictionary.TryGetValue(Name, out var value))
        {
            return value;
        }

        throw new InvalidOperationException(
            $"The JSONColdStore property '{Name}' is not available on the shared-type entity instance.");
    }

    internal void SetValue(object entity, object? value)
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (PropertyInfo is not null)
        {
            PropertyInfo.SetValue(entity, value);
            return;
        }

        if (entity is IDictionary<string, object?> dictionary)
        {
            dictionary[Name] = value;
            return;
        }

        if (entity is IDictionary<string, object> objectDictionary)
        {
            objectDictionary[Name] = value!;
            return;
        }

        throw new InvalidOperationException(
            $"The JSONColdStore property '{Name}' cannot be assigned on the shared-type entity instance.");
    }
}

internal sealed record JsonColdStoreKeyDescriptor(IReadOnlyList<JsonColdStorePropertyDescriptor> Properties)
{
    internal IReadOnlyList<string> PropertyNames { get; } = Properties.Select(property => property.Name).ToArray();

    internal IReadOnlyList<Type> ClrTypes { get; } = Properties.Select(property => property.ClrType).ToArray();

    internal string CreateRecordId(object? keyValue)
    {
        if (Properties.Count == 1)
            return CreateRecordIdPart(Properties[0].Name, keyValue);

        if (keyValue is IEnumerable<object?> keyValues)
            return CreateRecordId(keyValues.ToArray());

        throw new InvalidOperationException(
            "Composite JSONColdStore primary keys require one key value per key property.");
    }

    internal string CreateRecordIdFromEntity(object entity) =>
        CreateRecordId(Properties.Select(property => property.GetValue(entity)).ToArray());

    private string CreateRecordId(IReadOnlyList<object?> keyValues)
    {
        if (keyValues.Count != Properties.Count)
        {
            throw new InvalidOperationException(
                "Composite JSONColdStore primary key value count must match the key property count.");
        }

        return string.Join(
            "\u001F",
            keyValues.Select((value, index) => CreateRecordIdPart(Properties[index].Name, value)));
    }

    private static string CreateRecordIdPart(string propertyName, object? keyValue)
    {
        if (keyValue is null)
            throw new InvalidOperationException(
                $"The JSONColdStore primary key '{propertyName}' cannot be null.");

        var value = Convert.ToString(keyValue, CultureInfo.InvariantCulture);
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException(
                $"The JSONColdStore primary key '{propertyName}' cannot be empty.");

        return value;
    }
}

internal sealed record JsonColdStoreIndexDescriptor(
    string[] PropertyNames,
    JsonColdStorePropertyDescriptor[] Properties,
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

internal sealed record JsonColdStoreReferenceNavigationDescriptor(
    string Name,
    Type TargetClrType,
    PropertyInfo PropertyInfo,
    IReadOnlyList<JsonColdStorePropertyDescriptor> ForeignKeyProperties,
    IReadOnlyList<string> PrincipalKeyPropertyNames)
{
    internal object? CreateTargetKeyValue(object entity)
    {
        var keyValues = ForeignKeyProperties
            .Select(property => property.GetValue(entity))
            .ToArray();
        return keyValues.Any(value => value is null)
            ? null
            : keyValues.Length == 1
                ? keyValues[0]
                : keyValues;
    }

    internal void SetValue(object entity, object? value)
    {
        ArgumentNullException.ThrowIfNull(entity);
        PropertyInfo.SetValue(entity, value);
    }
}
