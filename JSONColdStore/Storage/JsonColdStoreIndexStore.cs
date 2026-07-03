using System.Text.Json;

namespace JSONColdStore.Storage;

internal sealed class JsonColdStoreIndexStore
{
    private static readonly JsonSerializerOptions IndexJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly JsonColdStoreOptions _options;
    private readonly bool _protectDocuments;

    internal JsonColdStoreIndexStore(JsonColdStoreOptions options, bool protectDocuments)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _protectDocuments = protectDocuments;
    }

    internal async Task UpsertAsync(
        string entityName,
        string indexName,
        string indexKey,
        string recordId,
        CancellationToken cancellationToken = default)
    {
        ValidateIndexNameAndRecordId(indexName, recordId);

        var document = await ReadDocumentAsync(entityName, indexName, cancellationToken);
        RemoveRecordId(document, recordId);

        if (!document.Buckets.TryGetValue(indexKey, out var bucket))
        {
            bucket = [];
            document.Buckets[indexKey] = bucket;
        }

        if (!bucket.Contains(recordId, StringComparer.Ordinal))
            bucket.Add(recordId);

        await WriteDocumentAsync(entityName, indexName, Normalize(document), cancellationToken);
    }

    internal async Task RemoveAsync(
        string entityName,
        string indexName,
        string recordId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(indexName))
            throw new ArgumentException("An index name is required.", nameof(indexName));
        if (string.IsNullOrWhiteSpace(recordId))
            throw new ArgumentException("A record id is required.", nameof(recordId));

        var document = await ReadDocumentAsync(entityName, indexName, cancellationToken);
        RemoveRecordId(document, recordId);
        await WriteDocumentAsync(entityName, indexName, Normalize(document), cancellationToken);
    }

    internal async Task ReplaceAsync(
        string entityName,
        string indexName,
        IReadOnlyDictionary<string, IReadOnlyList<string>> buckets,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(indexName))
            throw new ArgumentException("An index name is required.", nameof(indexName));
        ArgumentNullException.ThrowIfNull(buckets);

        var document = new JsonColdStoreIndexDocument(
            buckets.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.ToList(),
                StringComparer.Ordinal));

        await WriteDocumentAsync(entityName, indexName, Normalize(document), cancellationToken);
    }

    internal async Task<IReadOnlyList<string>> ReadRecordIdsAsync(
        string entityName,
        string indexName,
        string indexKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(indexName))
            throw new ArgumentException("An index name is required.", nameof(indexName));

        var document = await ReadDocumentAsync(entityName, indexName, cancellationToken);
        return document.Buckets.TryGetValue(indexKey, out var bucket)
            ? bucket.Order(StringComparer.Ordinal).ToArray()
            : [];
    }

    internal bool DocumentExists(string entityName, string indexName)
    {
        if (string.IsNullOrWhiteSpace(indexName))
            throw new ArgumentException("An index name is required.", nameof(indexName));

        var path = GetIndexPath(entityName, indexName);
        return IndexDocumentFileExists(path);
    }

    internal async Task<IReadOnlyList<string>> ReadAllRecordIdsAsync(
        string entityName,
        string indexName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(indexName))
            throw new ArgumentException("An index name is required.", nameof(indexName));

        var document = await ReadDocumentAsync(entityName, indexName, cancellationToken);
        return document.Buckets.Values
            .SelectMany(recordIds => recordIds)
            .Where(recordId => !string.IsNullOrWhiteSpace(recordId))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    internal async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> ReadBucketsAsync(
        string entityName,
        string indexName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(indexName))
            throw new ArgumentException("An index name is required.", nameof(indexName));

        var document = Normalize(await ReadDocumentAsync(entityName, indexName, cancellationToken));
        return document.Buckets.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<string>)pair.Value,
            StringComparer.Ordinal);
    }

    private async Task<JsonColdStoreIndexDocument> ReadDocumentAsync(
        string entityName,
        string indexName,
        CancellationToken cancellationToken)
    {
        var path = GetIndexPath(entityName, indexName);
        if (!IndexDocumentFileExists(path))
            return new JsonColdStoreIndexDocument(new Dictionary<string, List<string>>(StringComparer.Ordinal));

        var bytes = await JsonColdStoreFileReader.ReadAllBytesAsync(_options, path, cancellationToken);
        var json = DecodeDocument(bytes);
        var document = JsonSerializer.Deserialize<JsonColdStoreIndexDocument>(
            json,
            IndexJsonOptions);

        return document ?? new JsonColdStoreIndexDocument(new Dictionary<string, List<string>>(StringComparer.Ordinal));
    }

    private async Task WriteDocumentAsync(
        string entityName,
        string indexName,
        JsonColdStoreIndexDocument document,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(document, IndexJsonOptions);
        var bytes = EncodeDocument(json);
        await JsonColdStoreAtomicFileWriter.WriteAsync(
            _options,
            GetIndexPathSegments(entityName, indexName),
            bytes,
            cancellationToken);
    }

    private byte[] EncodeDocument(ReadOnlySpan<byte> json) =>
        _protectDocuments
            ? JsonColdStorePayloadCodec.Encode(json, _options)
            : json.ToArray();

    private byte[] DecodeDocument(ReadOnlySpan<byte> bytes) =>
        _protectDocuments && JsonColdStorePayloadCodec.IsEnvelope(bytes)
            ? JsonColdStorePayloadCodec.Decode(bytes, _options)
            : bytes.ToArray();

    private static void RemoveRecordId(JsonColdStoreIndexDocument document, string recordId)
    {
        foreach (var key in document.Buckets.Keys.ToArray())
        {
            document.Buckets[key] = document.Buckets[key]
                .Where(existing => !string.Equals(existing, recordId, StringComparison.Ordinal))
                .ToList();

            if (document.Buckets[key].Count == 0)
                document.Buckets.Remove(key);
        }
    }

    private static JsonColdStoreIndexDocument Normalize(JsonColdStoreIndexDocument document)
    {
        var buckets = document.Buckets
            .Where(pair => pair.Value.Count > 0)
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(
                pair => pair.Key,
                pair => pair.Value.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(),
                StringComparer.Ordinal);

        return new JsonColdStoreIndexDocument(buckets);
    }

    private string GetIndexPath(string entityName, string indexName) =>
        JsonColdStorePathValidator.GetSafeChildPath(
            _options.DatabaseDirectory,
            [.. GetIndexPathSegments(entityName, indexName)]);

    private static bool IndexDocumentFileExists(string path)
    {
        JsonColdStoreFileGuard.ThrowIfReparsePoint(
            path,
            "The JSONColdStore index document cannot be a reparse point.");
        return File.Exists(path);
    }

    private static string[] GetIndexPathSegments(string entityName, string indexName) =>
    [
        "entities",
        JsonColdStoreNameEncoder.EncodePathSegment(entityName),
        "indexes",
        JsonColdStoreNameEncoder.EncodePathSegment(indexName) + ".json",
    ];

    private static void ValidateIndexNameAndRecordId(string indexName, string recordId)
    {
        if (string.IsNullOrWhiteSpace(indexName))
            throw new ArgumentException("An index name is required.", nameof(indexName));
        if (string.IsNullOrWhiteSpace(recordId))
            throw new ArgumentException("A record id is required.", nameof(recordId));
    }
}

internal sealed record JsonColdStoreIndexDocument(Dictionary<string, List<string>> Buckets);
