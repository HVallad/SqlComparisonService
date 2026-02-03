using SqlSyncService.Domain.Changes;

namespace SqlSyncService.Persistence;

public interface IPendingChangeRepository
{
    Task AddAsync(DetectedChange change, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DetectedChange>> GetPendingForSubscriptionAsync(Guid subscriptionId, CancellationToken cancellationToken = default);
    Task MarkAsProcessedAsync(Guid id, CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes processed pending changes older than the specified date.
    /// </summary>
    Task<int> DeleteProcessedOlderThanAsync(DateTime cutoffDate, CancellationToken cancellationToken = default);
}
