using SqlSyncService.Domain.Caching;
using SqlSyncService.Domain.Comparisons;

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

    /// <summary>
    /// Updates an existing snapshot with new or modified objects.
    /// If an object with the same schema, name, and type exists, it is replaced.
    /// Otherwise, the object is added.
    /// </summary>
    Task UpdateObjectsAsync(Guid snapshotId, IEnumerable<SchemaObjectSummary> updatedObjects, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a specific object from a snapshot (for handling deleted objects).
    /// </summary>
    Task RemoveObjectAsync(Guid snapshotId, string schemaName, string objectName, SqlObjectType objectType, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the snapshot with a new set of objects and persists changes.
    /// Also updates the snapshot hash and captured timestamp.
    /// </summary>
    Task UpdateAsync(SchemaSnapshot snapshot, CancellationToken cancellationToken = default);
}
