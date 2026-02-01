using SqlSyncService.Domain.Changes;

namespace SqlSyncService.Persistence;

public interface IPendingChangeRepository
{
    Task AddAsync(DetectedChange change, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DetectedChange>> GetPendingForSubscriptionAsync(Guid subscriptionId, CancellationToken cancellationToken = default);
    Task MarkAsProcessedAsync(Guid id, CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}

