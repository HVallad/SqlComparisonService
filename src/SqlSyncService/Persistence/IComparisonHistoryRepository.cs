using SqlSyncService.Domain.Comparisons;

namespace SqlSyncService.Persistence;

public interface IComparisonHistoryRepository
{
    Task AddAsync(ComparisonResult result, CancellationToken cancellationToken = default);
    Task<ComparisonResult?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ComparisonResult>> GetBySubscriptionAsync(Guid subscriptionId, int? maxCount = null, CancellationToken cancellationToken = default);
    Task<int> DeleteBySubscriptionAsync(Guid subscriptionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes comparison history older than the specified date.
    /// </summary>
    Task<int> DeleteOlderThanAsync(DateTime cutoffDate, CancellationToken cancellationToken = default);
}
