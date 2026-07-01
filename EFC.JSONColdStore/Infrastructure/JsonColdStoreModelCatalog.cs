using System.Security.Cryptography;
using System.Text.Json;
using EFC.JSONColdStore.Storage;

namespace EFC.JSONColdStore.Infrastructure;

internal sealed class JsonColdStoreModelCatalog
{
    private const int CurrentFormatVersion = 1;
    internal const string ModelFileName = "_model.json";

    private static readonly JsonSerializerOptions CatalogJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private static readonly JsonSerializerOptions HashJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    private readonly JsonColdStoreOptions _options;
    private readonly bool _protectDocument;

    internal JsonColdStoreModelCatalog(JsonColdStoreOptions options, bool protectDocument)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _protectDocument = protectDocument;
    }

    internal async Task<bool> EnsureCompatibleAsync(
        JsonColdStoreModelDescriptor descriptor,
        bool createIfMissing,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        var snapshot = JsonColdStoreModelSnapshot.FromDescriptor(descriptor);
        var expectedHash = ComputeHash(snapshot);
        var modelPath = GetModelFilePath();

        if (!File.Exists(modelPath))
        {
            if (!createIfMissing)
                return false;

            var document = new JsonColdStoreModelDocument
            {
                FormatVersion = CurrentFormatVersion,
                CreatedAt = DateTimeOffset.UtcNow,
                ProviderVersion = JsonColdStoreProviderInfo.Version,
                ModelHash = expectedHash,
                Model = snapshot,
            };

            await WriteAsync(document, cancellationToken);
            return true;
        }

        var stored = await ReadAsync(cancellationToken);
        Validate(stored, expectedHash);
        return true;
    }

    private static string ComputeHash(JsonColdStoreModelSnapshot snapshot)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, HashJsonOptions);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private async Task<JsonColdStoreModelDocument> ReadAsync(CancellationToken cancellationToken)
    {
        var bytes = await JsonColdStoreAtomicFileWriter.ReadAsync(
            _options,
            [ModelFileName],
            cancellationToken);
        var json = DecodeDocument(bytes);
        var document = JsonSerializer.Deserialize<JsonColdStoreModelDocument>(
            json,
            CatalogJsonOptions);

        return document
            ?? throw new InvalidDataException("The JSONColdStore model catalog file is empty.");
    }

    private async Task WriteAsync(
        JsonColdStoreModelDocument document,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(document, CatalogJsonOptions);
        var bytes = EncodeDocument(json);
        await JsonColdStoreAtomicFileWriter.WriteAsync(
            _options.DatabaseDirectory,
            [ModelFileName],
            bytes,
            _options.FsyncOnWrite,
            cancellationToken);
    }

    private byte[] EncodeDocument(ReadOnlySpan<byte> json) =>
        _protectDocument
            ? JsonColdStorePayloadCodec.Encode(json, _options)
            : json.ToArray();

    private byte[] DecodeDocument(ReadOnlySpan<byte> bytes) =>
        _protectDocument && JsonColdStorePayloadCodec.IsEnvelope(bytes)
            ? JsonColdStorePayloadCodec.Decode(bytes, _options)
            : bytes.ToArray();

    private static void Validate(JsonColdStoreModelDocument stored, string expectedHash)
    {
        if (stored.FormatVersion != CurrentFormatVersion)
        {
            throw new NotSupportedException(
                $"JSONColdStore model catalog format version {stored.FormatVersion} is not supported.");
        }

        if (stored.CreatedAt == default)
            throw new InvalidDataException("The JSONColdStore model catalog creation timestamp is invalid.");

        if (string.IsNullOrWhiteSpace(stored.ProviderVersion))
            throw new InvalidDataException("The JSONColdStore model catalog provider version is required.");

        if (string.IsNullOrWhiteSpace(stored.ModelHash))
            throw new InvalidDataException("The JSONColdStore model catalog does not contain a model hash.");

        if (stored.Model is null)
            throw new InvalidDataException("The JSONColdStore model catalog does not contain a model snapshot.");

        ValidateSnapshot(stored.Model);

        var storedModelHash = ComputeHash(stored.Model);
        if (!string.Equals(stored.ModelHash, storedModelHash, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The JSONColdStore model catalog hash does not match its model snapshot.");
        }

        if (!string.Equals(stored.ModelHash, expectedHash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The configured EF model does not match the JSONColdStore model catalog for this database directory.");
        }
    }

    private static void ValidateSnapshot(JsonColdStoreModelSnapshot snapshot)
    {
        if (snapshot.Entities is null)
            throw new InvalidDataException("The JSONColdStore model catalog entity list is required.");

        var entityNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entity in snapshot.Entities)
        {
            if (entity is null)
                throw new InvalidDataException("The JSONColdStore model catalog contains an empty entity.");

            if (string.IsNullOrWhiteSpace(entity.EntityName))
                throw new InvalidDataException("The JSONColdStore model catalog entity name is required.");

            if (!entityNames.Add(entity.EntityName))
            {
                throw new InvalidDataException(
                    $"The JSONColdStore model catalog contains duplicate entity '{entity.EntityName}'.");
            }

            if (string.IsNullOrWhiteSpace(entity.ClrTypeName))
                throw new InvalidDataException("The JSONColdStore model catalog entity CLR type is required.");

            if (entity.Key is null)
                throw new InvalidDataException("The JSONColdStore model catalog entity key is required.");

            ValidateKey(entity.Key);

            if (entity.Properties is null)
                throw new InvalidDataException("The JSONColdStore model catalog entity property list is required.");

            ValidateProperties(entity.Properties);

            if (entity.Indexes is null)
                throw new InvalidDataException("The JSONColdStore model catalog entity index list is required.");

            foreach (var index in entity.Indexes)
                ValidateIndex(index);
        }
    }

    private static void ValidateKey(JsonColdStoreKeySnapshot key)
    {
        if (key.PropertyNames is null || key.PropertyNames.Count == 0)
            throw new InvalidDataException("The JSONColdStore model catalog key property list is required.");

        if (key.PropertyTypeNames is null)
            throw new InvalidDataException("The JSONColdStore model catalog key property type list is required.");

        if (key.PropertyTypeNames.Count != key.PropertyNames.Count)
        {
            throw new InvalidDataException(
                "The JSONColdStore model catalog key property type count must match the property count.");
        }

        var propertyNames = new HashSet<string>(StringComparer.Ordinal);
        for (var propertyIndex = 0; propertyIndex < key.PropertyNames.Count; propertyIndex++)
        {
            var propertyName = key.PropertyNames[propertyIndex];
            if (string.IsNullOrWhiteSpace(propertyName))
                throw new InvalidDataException("The JSONColdStore model catalog key property name is required.");

            if (!propertyNames.Add(propertyName))
            {
                throw new InvalidDataException(
                    $"The JSONColdStore model catalog contains duplicate key property '{propertyName}'.");
            }

            if (string.IsNullOrWhiteSpace(key.PropertyTypeNames[propertyIndex]))
                throw new InvalidDataException("The JSONColdStore model catalog key property type name is required.");
        }
    }

    private static void ValidateProperties(IReadOnlyList<JsonColdStorePropertySnapshot> properties)
    {
        var propertyNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in properties)
        {
            if (property is null)
                throw new InvalidDataException("The JSONColdStore model catalog contains an empty property.");

            if (string.IsNullOrWhiteSpace(property.Name))
                throw new InvalidDataException("The JSONColdStore model catalog property name is required.");

            if (!propertyNames.Add(property.Name))
            {
                throw new InvalidDataException(
                    $"The JSONColdStore model catalog contains duplicate property '{property.Name}'.");
            }

            if (string.IsNullOrWhiteSpace(property.ClrTypeName))
                throw new InvalidDataException("The JSONColdStore model catalog property CLR type is required.");
        }
    }

    private static void ValidateIndex(JsonColdStoreIndexSnapshot index)
    {
        if (index is null)
            throw new InvalidDataException("The JSONColdStore model catalog contains an empty index.");

        if (index.PropertyNames is null || index.PropertyNames.Count == 0)
            throw new InvalidDataException("The JSONColdStore model catalog index property list is required.");

        if (index.PropertyTypeNames is null)
            throw new InvalidDataException("The JSONColdStore model catalog index property type list is required.");

        if (index.PropertyTypeNames.Count != index.PropertyNames.Count)
        {
            throw new InvalidDataException(
                "The JSONColdStore model catalog index property type count must match the property count.");
        }

        var propertyNames = new HashSet<string>(StringComparer.Ordinal);
        for (var propertyIndex = 0; propertyIndex < index.PropertyNames.Count; propertyIndex++)
        {
            var propertyName = index.PropertyNames[propertyIndex];
            if (string.IsNullOrWhiteSpace(propertyName))
                throw new InvalidDataException("The JSONColdStore model catalog index property name is required.");

            if (!propertyNames.Add(propertyName))
            {
                throw new InvalidDataException(
                    $"The JSONColdStore model catalog contains duplicate index property '{propertyName}'.");
            }

            if (string.IsNullOrWhiteSpace(index.PropertyTypeNames[propertyIndex]))
            {
                throw new InvalidDataException(
                    "The JSONColdStore model catalog index property type name is required.");
            }
        }
    }

    private string GetModelFilePath() =>
        JsonColdStorePathValidator.GetSafeChildPath(_options.DatabaseDirectory, ModelFileName);
}

internal sealed record JsonColdStoreModelDocument
{
    public int FormatVersion { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public required string ProviderVersion { get; init; }

    public required string ModelHash { get; init; }

    public required JsonColdStoreModelSnapshot Model { get; init; }
}

internal sealed record JsonColdStoreModelSnapshot
{
    public required IReadOnlyList<JsonColdStoreEntitySnapshot> Entities { get; init; }

    internal static JsonColdStoreModelSnapshot FromDescriptor(JsonColdStoreModelDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        return new JsonColdStoreModelSnapshot
        {
            Entities = descriptor.Entities
                .Select(entity => new JsonColdStoreEntitySnapshot
                {
                    EntityName = entity.EntityName,
                    ClrTypeName = entity.ClrType.FullName ?? entity.ClrType.Name,
                    Key = new JsonColdStoreKeySnapshot
                    {
                        PropertyNames = entity.Key.PropertyNames,
                        PropertyTypeNames = entity.Key.ClrTypes
                            .Select(type => type.FullName ?? type.Name)
                            .ToArray(),
                    },
                    Properties = entity.Properties
                        .Select(property => new JsonColdStorePropertySnapshot
                        {
                            Name = property.Name,
                            ClrTypeName = property.ClrType.FullName ?? property.ClrType.Name,
                        })
                        .ToArray(),
                    Indexes = entity.Indexes
                        .Select(index => new JsonColdStoreIndexSnapshot
                        {
                            PropertyNames = index.PropertyNames,
                            PropertyTypeNames = index.Properties
                                .Select(property => property.ClrType.FullName ?? property.ClrType.Name)
                                .ToArray(),
                            IsUnique = index.IsUnique,
                        })
                        .ToArray(),
                })
                .ToArray(),
        };
    }
}

internal sealed record JsonColdStoreEntitySnapshot
{
    public required string EntityName { get; init; }

    public required string ClrTypeName { get; init; }

    public required JsonColdStoreKeySnapshot Key { get; init; }

    public required IReadOnlyList<JsonColdStorePropertySnapshot> Properties { get; init; }

    public required IReadOnlyList<JsonColdStoreIndexSnapshot> Indexes { get; init; }
}

internal sealed record JsonColdStoreKeySnapshot
{
    public required IReadOnlyList<string> PropertyNames { get; init; }

    public required IReadOnlyList<string> PropertyTypeNames { get; init; }
}

internal sealed record JsonColdStorePropertySnapshot
{
    public required string Name { get; init; }

    public required string ClrTypeName { get; init; }
}

internal sealed record JsonColdStoreIndexSnapshot
{
    public required IReadOnlyList<string> PropertyNames { get; init; }

    public required IReadOnlyList<string> PropertyTypeNames { get; init; }

    public bool IsUnique { get; init; }
}
