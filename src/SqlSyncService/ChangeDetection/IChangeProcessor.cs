using SqlSyncService.Domain.Changes;

namespace SqlSyncService.ChangeDetection;

/// <summary>
/// Processes batches of detected changes and triggers comparisons based on subscription options.
/// </summary>
public interface IChangeProcessor
{
    /// <summary>
    /// Processes a batch of detected changes. Persists changes to the repository and
    /// triggers comparisons if the subscription options allow.
    /// </summary>
    /// <param name="batch">The batch of changes to process.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ProcessBatchAsync(PendingChangeBatch batch, CancellationToken cancellationToken = default);
}

