using System.Data;
using System.Data.Common;
using JSONColdStore.Storage;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace JSONColdStore.Infrastructure;

internal sealed class JsonColdStoreTransactionManager :
    IDbContextTransactionManager,
    IRelationalTransactionManager
{
    private readonly JsonColdStoreOptions _options;
    private JsonColdStoreTransaction? _currentTransaction;

    public JsonColdStoreTransactionManager(IDbContextOptions contextOptions)
    {
        ArgumentNullException.ThrowIfNull(contextOptions);
        _options = contextOptions.FindExtension<JsonColdStoreOptionsExtension>()?.Options
            ?? throw new InvalidOperationException("JSONColdStore options are not configured.");
    }

    public IDbContextTransaction? CurrentTransaction => _currentTransaction;

    internal JsonColdStoreTransaction? CurrentJsonColdStoreTransaction => _currentTransaction;

    public IDbContextTransaction BeginTransaction() =>
        BeginTransaction(IsolationLevel.Unspecified);

    public IDbContextTransaction BeginTransaction(IsolationLevel isolationLevel) =>
        BeginTransactionAsync(isolationLevel).GetAwaiter().GetResult();

    public Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
        BeginTransactionAsync(IsolationLevel.Unspecified, cancellationToken);

    public async Task<IDbContextTransaction> BeginTransactionAsync(
        IsolationLevel isolationLevel,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_currentTransaction is not null)
            throw new InvalidOperationException("JSONColdStore already has an active transaction for this context.");

        var writerLock = await JsonColdStoreDatabaseLock.AcquireAsync(_options, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var transaction = new JsonColdStoreTransaction(
                this,
                _options,
                writerLock,
                isolationLevel);
            _currentTransaction = transaction;
            return transaction;
        }
        catch
        {
            writerLock.Dispose();
            throw;
        }
    }

    public void CommitTransaction()
    {
        var transaction = RequireCurrentTransaction();
        transaction.Commit();
    }

    public Task CommitTransactionAsync(CancellationToken cancellationToken = default)
    {
        var transaction = RequireCurrentTransaction();
        return transaction.CommitAsync(cancellationToken);
    }

    public void RollbackTransaction()
    {
        var transaction = RequireCurrentTransaction();
        transaction.Rollback();
    }

    public Task RollbackTransactionAsync(CancellationToken cancellationToken = default)
    {
        var transaction = RequireCurrentTransaction();
        return transaction.RollbackAsync(cancellationToken);
    }

    public IDbContextTransaction? UseTransaction(DbTransaction? transaction) =>
        throw Unsupported("External DbTransaction instances are not supported by JSONColdStore.");

    public IDbContextTransaction? UseTransaction(DbTransaction? transaction, Guid transactionId) =>
        throw Unsupported("External DbTransaction instances are not supported by JSONColdStore.");

    public Task<IDbContextTransaction?> UseTransactionAsync(
        DbTransaction? transaction,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromException<IDbContextTransaction?>(
            Unsupported("External DbTransaction instances are not supported by JSONColdStore."));
    }

    public Task<IDbContextTransaction?> UseTransactionAsync(
        DbTransaction? transaction,
        Guid transactionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromException<IDbContextTransaction?>(
            Unsupported("External DbTransaction instances are not supported by JSONColdStore."));
    }

    internal void EnqueueSaveBatch(JsonColdStoreTransactionalSaveBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        RequireCurrentTransaction().Enqueue(batch);
    }

    internal void Clear(JsonColdStoreTransaction transaction)
    {
        if (ReferenceEquals(_currentTransaction, transaction))
            _currentTransaction = null;
    }

    public void ResetState()
    {
        _currentTransaction?.Rollback();
        _currentTransaction = null;
    }

    public async Task ResetStateAsync(CancellationToken cancellationToken = default)
    {
        if (_currentTransaction is not null)
            await _currentTransaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
        _currentTransaction = null;
    }

    private JsonColdStoreTransaction RequireCurrentTransaction() =>
        _currentTransaction
        ?? throw new InvalidOperationException("JSONColdStore does not have an active transaction.");

    private static NotSupportedException Unsupported(string message) =>
        new("JSONColdStore EF provider support is incomplete: " + message);
}

internal sealed class JsonColdStoreTransaction : IDbContextTransaction
{
    private readonly JsonColdStoreTransactionManager _manager;
    private readonly JsonColdStoreOptions _options;
    private readonly JsonColdStoreDatabaseLock _writerLock;
    private readonly List<JsonColdStoreTransactionalSaveBatch> _batches = [];
    private JsonColdStoreModelDescriptor? _modelDescriptor;
    private bool _completed;
    private bool _disposed;

    internal JsonColdStoreTransaction(
        JsonColdStoreTransactionManager manager,
        JsonColdStoreOptions options,
        JsonColdStoreDatabaseLock writerLock,
        IsolationLevel isolationLevel)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _writerLock = writerLock ?? throw new ArgumentNullException(nameof(writerLock));
        IsolationLevel = isolationLevel;
        TransactionId = Guid.NewGuid();
    }

    public Guid TransactionId { get; }

    public bool SupportsSavepoints => false;

    internal IsolationLevel IsolationLevel { get; }

    internal void Enqueue(JsonColdStoreTransactionalSaveBatch batch)
    {
        ThrowIfCompleted();
        if (batch.Operations.Count == 0)
            return;

        if (_modelDescriptor is null)
        {
            _modelDescriptor = batch.ModelDescriptor;
        }
        else if (!ReferenceEquals(_modelDescriptor, batch.ModelDescriptor))
        {
            throw new InvalidOperationException(
                "JSONColdStore transactions cannot mix multiple EF models.");
        }

        _batches.Add(batch);
    }

    public void Commit() => CommitAsync().GetAwaiter().GetResult();

    public async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfCompleted();
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            if (_modelDescriptor is not null)
            {
                var operations = BuildFinalOperations();
                await using var session = await JsonColdStoreDatabaseSession
                    .OpenWithExistingWriterLockAsync(_options, _writerLock, cancellationToken)
                    .ConfigureAwait(false);
                var entityStore = new JsonColdStoreEntityRecordStore(session, _modelDescriptor);
                await CommitOperationsAsync(entityStore, operations, cancellationToken)
                    .ConfigureAwait(false);
            }

            Complete();
        }
        catch
        {
            Complete();
            throw;
        }
    }

    public void Rollback()
    {
        if (_completed)
            return;

        _batches.Clear();
        Complete();
    }

    public Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Rollback();
        return Task.CompletedTask;
    }

    public void CreateSavepoint(string name) =>
        throw UnsupportedSavepoints();

    public Task CreateSavepointAsync(string name, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromException(UnsupportedSavepoints());
    }

    public void RollbackToSavepoint(string name) =>
        throw UnsupportedSavepoints();

    public Task RollbackToSavepointAsync(string name, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromException(UnsupportedSavepoints());
    }

    public void ReleaseSavepoint(string name) =>
        throw UnsupportedSavepoints();

    public Task ReleaseSavepointAsync(string name, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromException(UnsupportedSavepoints());
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        if (!_completed)
            Rollback();
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        await ValueTask.CompletedTask;
    }

    private IReadOnlyList<JsonColdStoreTransactionalOperation> BuildFinalOperations()
    {
        var operationsByRecord = new Dictionary<string, JsonColdStoreTransactionalOperation>(
            StringComparer.Ordinal);

        foreach (var batch in _batches)
        {
            foreach (var operation in batch.Operations)
            {
                var key = operation.Descriptor.EntityName + "\u001F" + operation.RecordId;
                operationsByRecord[key] = operation;
            }
        }

        return operationsByRecord.Values
            .OrderBy(operation => operation.Descriptor.EntityName, StringComparer.Ordinal)
            .ThenBy(operation => operation.RecordId, StringComparer.Ordinal)
            .ToArray();
    }

    private static async Task CommitOperationsAsync(
        JsonColdStoreEntityRecordStore entityStore,
        IReadOnlyList<JsonColdStoreTransactionalOperation> operations,
        CancellationToken cancellationToken)
    {
        var writes = operations.OfType<JsonColdStoreTransactionalWrite>().ToArray();
        var deletedRecordIdsByEntity = operations
            .OfType<JsonColdStoreTransactionalDelete>()
            .GroupBy(operation => operation.Descriptor.EntityName, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(operation => operation.RecordId).ToHashSet(StringComparer.Ordinal),
                StringComparer.Ordinal);

        EnsureNoDuplicateUniqueIndexValues(writes);

        foreach (var write in writes)
        {
            deletedRecordIdsByEntity.TryGetValue(
                write.Descriptor.EntityName,
                out var ignoredRecordIds);
            await entityStore.ValidateUniqueIndexesAsync(
                write,
                ignoredRecordIds,
                cancellationToken).ConfigureAwait(false);
        }

        foreach (var delete in operations.OfType<JsonColdStoreTransactionalDelete>())
        {
            await entityStore.DeleteEntityAsync(delete, cancellationToken)
                .ConfigureAwait(false);
        }

        foreach (var write in writes)
        {
            deletedRecordIdsByEntity.TryGetValue(
                write.Descriptor.EntityName,
                out var ignoredRecordIds);
            await entityStore.WriteEntityAsync(
                write,
                ignoredRecordIds,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static void EnsureNoDuplicateUniqueIndexValues(
        IReadOnlyList<JsonColdStoreTransactionalWrite> writes)
    {
        var pendingUniqueKeys = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var write in writes)
        {
            foreach (var indexValue in write.IndexValues.Where(value => value.Index.IsUnique))
            {
                var pendingKey = string.Join(
                    '\u001F',
                    write.Descriptor.EntityName,
                    indexValue.Index.StorageName,
                    indexValue.IndexKey);

                if (pendingUniqueKeys.TryGetValue(pendingKey, out var existingRecordId)
                    && !string.Equals(existingRecordId, write.RecordId, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"The JSONColdStore unique index '{indexValue.Index.StorageName}' for entity "
                        + $"'{write.Descriptor.EntityName}' contains a duplicate value "
                        + "inside the current transaction.");
                }

                pendingUniqueKeys[pendingKey] = write.RecordId;
            }
        }
    }

    private void Complete()
    {
        _completed = true;
        _batches.Clear();
        _manager.Clear(this);
        _writerLock.Dispose();
    }

    private void ThrowIfCompleted()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_completed)
            throw new InvalidOperationException("The JSONColdStore transaction has already completed.");
    }

    private static NotSupportedException UnsupportedSavepoints() =>
        new("JSONColdStore EF provider support is incomplete: Savepoints are not implemented yet.");
}

internal sealed record JsonColdStoreTransactionalSaveBatch(
    JsonColdStoreModelDescriptor ModelDescriptor,
    IReadOnlyList<JsonColdStoreTransactionalOperation> Operations);

internal abstract record JsonColdStoreTransactionalOperation(
    JsonColdStoreEntityDescriptor Descriptor,
    string RecordId);

internal sealed record JsonColdStoreTransactionalWrite(
    JsonColdStoreEntityDescriptor Descriptor,
    string RecordId,
    byte[] Payload,
    IReadOnlyList<JsonColdStoreTransactionalIndexValue> IndexValues)
    : JsonColdStoreTransactionalOperation(Descriptor, RecordId);

internal sealed record JsonColdStoreTransactionalDelete(
    JsonColdStoreEntityDescriptor Descriptor,
    string RecordId)
    : JsonColdStoreTransactionalOperation(Descriptor, RecordId);

internal sealed record JsonColdStoreTransactionalIndexValue(
    JsonColdStoreIndexDescriptor Index,
    string IndexKey,
    IReadOnlyList<object?> Values);
