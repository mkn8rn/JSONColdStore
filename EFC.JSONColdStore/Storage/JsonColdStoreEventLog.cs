using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EFC.JSONColdStore.Storage;

internal sealed class JsonColdStoreEventLog
{
    private const int FormatVersion = 1;

    private static readonly JsonSerializerOptions EventJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    private readonly JsonColdStoreOptions _options;
    private readonly bool _protectEvents;

    internal JsonColdStoreEventLog(JsonColdStoreOptions options, bool protectEvents)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _protectEvents = protectEvents;
    }

    internal async Task AppendAsync(
        string eventType,
        string? entityName = null,
        string? recordId = null,
        Guid? manifestId = null,
        string? detail = null,
        CancellationToken cancellationToken = default)
    {
        if (!_options.EventLog.Enabled)
            return;

        try
        {
            await AppendCoreAsync(
                eventType,
                entityName,
                recordId,
                manifestId,
                detail,
                cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private async Task AppendCoreAsync(
        string eventType,
        string? entityName,
        string? recordId,
        Guid? manifestId,
        string? detail,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var entry = new JsonColdStoreEventLogEntry
        {
            CreatedAt = DateTimeOffset.UtcNow,
            EventType = eventType,
            EntityName = entityName,
            RecordIdHash = string.IsNullOrWhiteSpace(recordId)
                ? null
                : HashIdentifier(recordId),
            ManifestId = manifestId?.ToString("N"),
            Detail = detail,
        };
        var line = CreateLine(entry);
        var eventsDirectory = JsonColdStorePathValidator.GetSafeChildPath(
            _options.DatabaseDirectory,
            "_events");
        Directory.CreateDirectory(eventsDirectory);

        var eventFile = JsonColdStorePathValidator.GetSafeChildPath(
            eventsDirectory,
            DateTimeOffset.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".jsonl");
        await File.AppendAllTextAsync(
            eventFile,
            line + Environment.NewLine,
            Encoding.UTF8,
            cancellationToken);

        PruneExpiredLogs(eventsDirectory);
    }

    private string CreateLine(JsonColdStoreEventLogEntry entry)
    {
        if (!_protectEvents)
        {
            return JsonSerializer.Serialize(
                new JsonColdStoreEventLogLine
                {
                    FormatVersion = FormatVersion,
                    Protected = false,
                    Event = entry,
                },
                EventJsonOptions);
        }

        var eventJson = JsonSerializer.SerializeToUtf8Bytes(entry, EventJsonOptions);
        var protectedPayload = JsonColdStorePayloadCodec.Encode(eventJson, _options);
        return JsonSerializer.Serialize(
            new JsonColdStoreEventLogLine
            {
                FormatVersion = FormatVersion,
                Protected = true,
                Payload = Convert.ToBase64String(protectedPayload),
            },
            EventJsonOptions);
    }

    private void PruneExpiredLogs(string eventsDirectory)
    {
        if (_options.EventLog.Retention < TimeSpan.Zero)
            return;

        var cutoff = DateTimeOffset.UtcNow.Subtract(_options.EventLog.Retention).UtcDateTime;
        foreach (var file in Directory.EnumerateFiles(eventsDirectory, "*.jsonl"))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                    File.Delete(file);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static string HashIdentifier(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

internal sealed record JsonColdStoreEventLogLine
{
    public int FormatVersion { get; init; }

    public bool Protected { get; init; }

    public JsonColdStoreEventLogEntry? Event { get; init; }

    public string? Payload { get; init; }
}

internal sealed record JsonColdStoreEventLogEntry
{
    public DateTimeOffset CreatedAt { get; init; }

    public required string EventType { get; init; }

    public string? EntityName { get; init; }

    public string? RecordIdHash { get; init; }

    public string? ManifestId { get; init; }

    public string? Detail { get; init; }
}
