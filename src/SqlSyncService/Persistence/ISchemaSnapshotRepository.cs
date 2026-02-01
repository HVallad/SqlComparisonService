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
}

