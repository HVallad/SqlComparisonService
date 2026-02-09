using SqlSyncService.Domain.Changes;
using SqlSyncService.Domain.Comparisons;
using SqlSyncService.Persistence;
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
    private readonly ILogger<ChangeProcessor> _logger;

    public ChangeProcessor(
        ISubscriptionRepository subscriptionRepository,
        IPendingChangeRepository pendingChangeRepository,
            IComparisonOrchestrator comparisonOrchestrator,
            ILogger<ChangeProcessor> logger)
    {
        _subscriptionRepository = subscriptionRepository ?? throw new ArgumentNullException(nameof(subscriptionRepository));
        _pendingChangeRepository = pendingChangeRepository ?? throw new ArgumentNullException(nameof(pendingChangeRepository));
        _comparisonOrchestrator = comparisonOrchestrator ?? throw new ArgumentNullException(nameof(comparisonOrchestrator));
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

        // 2. Get subscription and check options
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

        // 5. Trigger comparison(s)
        // For database changes with known object types, use single-object comparison for efficiency.
        // For file changes or unknown object types, use full comparison.
        try
        {
            await TriggerComparisonsAsync(batch, cancellationToken);

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

    private async Task TriggerComparisonsAsync(PendingChangeBatch batch, CancellationToken cancellationToken)
    {
        // Separate changes into those with known object types (can use batched incremental comparison)
        // and those without (require full comparison)
        var dbChangesWithType = batch.Changes
            .Where(c => c.Source == ChangeSource.Database && c.ObjectType.HasValue)
            .ToList();

        var otherChanges = batch.Changes
            .Where(c => c.Source == ChangeSource.FileSystem || !c.ObjectType.HasValue)
            .ToList();

        // Process database changes with known types using batched incremental comparison
        if (dbChangesWithType.Count > 0)
        {
            // Convert to ObjectIdentifier for batched query
            var objectIdentifiers = dbChangesWithType
                .Select(c => ObjectIdentifier.Parse(c.ObjectIdentifier, c.ObjectType!.Value))
                .ToList();

            _logger.LogInformation(
                "Triggering batched comparison for {Count} changed object(s) in subscription {SubscriptionId}",
                objectIdentifiers.Count, batch.SubscriptionId);

            await _comparisonOrchestrator.CompareObjectsAsync(
                batch.SubscriptionId,
                objectIdentifiers,
                trigger: "database-change",
                cancellationToken);
        }

        // Process file changes or changes without known types using full comparison
        if (otherChanges.Count > 0)
        {
            var hasDbChanges = otherChanges.Any(c => c.Source == ChangeSource.Database);
            var hasFileChanges = otherChanges.Any(c => c.Source == ChangeSource.FileSystem);

            // Determine trigger based on change sources
            var trigger = hasDbChanges ? "database-change" : "file-change";

            _logger.LogInformation(
                "Triggering {ComparisonType} comparison for subscription {SubscriptionId} due to {Count} change(s)",
                hasDbChanges ? "full" : "incremental",
                batch.SubscriptionId,
                otherChanges.Count);

            await _comparisonOrchestrator.RunComparisonAsync(
                batch.SubscriptionId,
                fullComparison: hasDbChanges,
                trigger: trigger,
                cancellationToken);
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

