using Microsoft.AspNetCore.SignalR;
using SqlSyncService.Domain.Changes;
using SqlSyncService.Persistence;
using SqlSyncService.Realtime;
using SqlSyncService.Services;

namespace SqlSyncService.ChangeDetection;

/// <summary>
/// Processes batches of detected changes by persisting them and triggering comparisons
/// based on subscription options.
/// </summary>
public sealed class ChangeProcessor : IChangeProcessor
{
    private readonly ISubscriptionRepository _subscriptionRepository;
    private readonly IPendingChangeRepository _pendingChangeRepository;
    private readonly IComparisonOrchestrator _comparisonOrchestrator;
    private readonly IHubContext<SyncHub> _hubContext;
    private readonly ILogger<ChangeProcessor> _logger;

    public ChangeProcessor(
        ISubscriptionRepository subscriptionRepository,
        IPendingChangeRepository pendingChangeRepository,
        IComparisonOrchestrator comparisonOrchestrator,
        IHubContext<SyncHub> hubContext,
        ILogger<ChangeProcessor> logger)
    {
        _subscriptionRepository = subscriptionRepository ?? throw new ArgumentNullException(nameof(subscriptionRepository));
        _pendingChangeRepository = pendingChangeRepository ?? throw new ArgumentNullException(nameof(pendingChangeRepository));
        _comparisonOrchestrator = comparisonOrchestrator ?? throw new ArgumentNullException(nameof(comparisonOrchestrator));
        _hubContext = hubContext ?? throw new ArgumentNullException(nameof(hubContext));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task ProcessBatchAsync(PendingChangeBatch batch, CancellationToken cancellationToken = default)
    {
        if (batch.Changes.Count == 0)
        {
            return;
        }

        _logger.LogInformation(
            "Processing batch of {ChangeCount} changes for subscription {SubscriptionId}",
            batch.Changes.Count, batch.SubscriptionId);

        // 1. Persist all changes to PendingChanges collection
        foreach (var change in batch.Changes)
        {
            await _pendingChangeRepository.AddAsync(change, cancellationToken);
        }

        // 2. Notify via SignalR that changes were detected
        await _hubContext.Clients.All.SendAsync(
            "ChangesDetected",
            new
            {
                SubscriptionId = batch.SubscriptionId,
                ChangeCount = batch.Changes.Count,
                Changes = batch.Changes.Select(c => new
                {
                    c.ObjectIdentifier,
                    Source = c.Source.ToString(),
                    Type = c.Type.ToString()
                })
            },
            cancellationToken);

        // 3. Get subscription and check options
        var subscription = await _subscriptionRepository.GetByIdAsync(batch.SubscriptionId, cancellationToken);
        if (subscription is null)
        {
            _logger.LogWarning("Subscription {SubscriptionId} not found while processing changes", batch.SubscriptionId);
            return;
        }

        if (!subscription.IsActive())
        {
            _logger.LogDebug("Subscription {SubscriptionId} is not active, skipping comparison", batch.SubscriptionId);
            return;
        }

        // 4. Determine if comparison should be triggered
        var shouldCompare = ShouldTriggerComparison(subscription, batch);

        if (!shouldCompare)
        {
            _logger.LogDebug(
                "Subscription {SubscriptionId} options do not allow auto-comparison for these changes",
                batch.SubscriptionId);
            return;
        }

        // 5. Trigger comparison
        // Use fullComparison when database changes are detected to ensure we fetch
        // a fresh snapshot from the database. File changes can use cached snapshot
        // since the database hasn't changed.
        var hasDbChanges = batch.Changes.Any(c => c.Source == ChangeSource.Database);

        try
        {
            _logger.LogInformation(
                "Triggering {ComparisonType} comparison for subscription {SubscriptionId} due to detected changes",
                hasDbChanges ? "full" : "incremental",
                batch.SubscriptionId);

            await _comparisonOrchestrator.RunComparisonAsync(
                batch.SubscriptionId,
                fullComparison: hasDbChanges,
                cancellationToken);

            // Mark changes as processed
            foreach (var change in batch.Changes)
            {
                await _pendingChangeRepository.MarkAsProcessedAsync(change.Id, cancellationToken);
            }

            _logger.LogDebug("Marked {ChangeCount} changes as processed", batch.Changes.Count);
        }
        catch (ComparisonInProgressException)
        {
            // Leave changes unprocessed; they'll be picked up by reconciliation
            _logger.LogInformation(
                "Comparison already in progress for subscription {SubscriptionId}, changes will be picked up by reconciliation",
                batch.SubscriptionId);
        }
    }

    private bool ShouldTriggerComparison(Domain.Subscriptions.Subscription subscription, PendingChangeBatch batch)
    {
        if (!subscription.Options.AutoCompare)
        {
            return false;
        }

        var hasFileChanges = batch.Changes.Any(c => c.Source == ChangeSource.FileSystem);
        var hasDbChanges = batch.Changes.Any(c => c.Source == ChangeSource.Database);

        return (hasFileChanges && subscription.Options.CompareOnFileChange)
            || (hasDbChanges && subscription.Options.CompareOnDatabaseChange);
    }
}

