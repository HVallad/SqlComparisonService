using SqlSyncService.Domain.Caching;

namespace SqlSyncService.Persistence;

public interface ISchemaSnapshotRepository
{
    Task AddAsync(SchemaSnapshot snapshot, CancellationToken cancellationToken = default);
    Task<SchemaSnapshot?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SchemaSnapshot>> GetBySubscriptionAsync(Guid subscriptionId, CancellationToken cancellationToken = default);
    Task<SchemaSnapshot?> GetLatestForSubscriptionAsync(Guid subscriptionId, CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);
    Task DeleteForSubscriptionAsync(Guid subscriptionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes snapshots older than the specified date.
    /// </summary>
    Task<int> DeleteOlderThanAsync(DateTime cutoffDate, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes snapshots for a subscription exceeding the maximum count, keeping the most recent.
    /// </summary>
    Task<int> DeleteExcessForSubscriptionAsync(Guid subscriptionId, int maxCount, CancellationToken cancellationToken = default);
}
