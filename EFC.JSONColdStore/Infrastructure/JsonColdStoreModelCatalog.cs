using System.Security.Cryptography;
using System.Text.Json;
using EFC.JSONColdStore.Storage;

namespace EFC.JSONColdStore.Infrastructure;

internal sealed class JsonColdStoreModelCatalog
{
    private const int CurrentFormatVersion = 1;
    private const string ModelFileName = "_model.json";

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
            _options.DatabaseDirectory,
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
        _protectDocument
            ? JsonColdStorePayloadCodec.Decode(bytes, _options)
            : bytes.ToArray();

    private static void Validate(JsonColdStoreModelDocument stored, string expectedHash)
    {
        if (stored.FormatVersion != CurrentFormatVersion)
        {
            throw new NotSupportedException(
                $"JSONColdStore model catalog format version {stored.FormatVersion} is not supported.");
        }

        if (string.IsNullOrWhiteSpace(stored.ModelHash))
            throw new InvalidDataException("The JSONColdStore model catalog does not contain a model hash.");

        if (stored.Model is null)
            throw new InvalidDataException("The JSONColdStore model catalog does not contain a model snapshot.");

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
                        PropertyName = entity.Key.PropertyName,
                        ClrTypeName = entity.Key.ClrType.FullName ?? entity.Key.ClrType.Name,
                    },
                    Indexes = entity.Indexes
                        .Select(index => new JsonColdStoreIndexSnapshot
                        {
                            PropertyNames = index.PropertyNames,
                            PropertyTypeNames = index.Properties
                                .Select(property => property.PropertyType.FullName ?? property.PropertyType.Name)
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

    public required IReadOnlyList<JsonColdStoreIndexSnapshot> Indexes { get; init; }
}

internal sealed record JsonColdStoreKeySnapshot
{
    public required string PropertyName { get; init; }

    public required string ClrTypeName { get; init; }
}

internal sealed record JsonColdStoreIndexSnapshot
{
    public required IReadOnlyList<string> PropertyNames { get; init; }

    public required IReadOnlyList<string> PropertyTypeNames { get; init; }

    public bool IsUnique { get; init; }
}
