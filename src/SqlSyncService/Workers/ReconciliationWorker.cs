using Microsoft.Extensions.Options;
using SqlSyncService.Configuration;
using SqlSyncService.Domain.Subscriptions;
using SqlSyncService.Persistence;
using SqlSyncService.Services;

namespace SqlSyncService.Workers;

/// <summary>
/// Background worker that performs periodic full comparisons to catch any missed changes.
/// Runs reconciliation for subscriptions with AutoCompare enabled.
/// </summary>
public sealed class ReconciliationWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ReconciliationWorker> _logger;
    private readonly TimeSpan _reconciliationInterval;
    private readonly bool _enabled;
    private readonly Random _random = new();

    public ReconciliationWorker(
        IServiceProvider serviceProvider,
        IOptions<ServiceConfiguration> config,
        ILogger<ReconciliationWorker> logger)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _reconciliationInterval = config.Value.Monitoring.FullReconciliationInterval;
        _enabled = config.Value.Workers.EnableReconciliation;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            _logger.LogInformation("ReconciliationWorker is disabled via configuration");
            return;
        }

        _logger.LogInformation("ReconciliationWorker starting with interval {Interval}", _reconciliationInterval);

        // Initial delay to let service stabilize
        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);

        using var timer = new PeriodicTimer(_reconciliationInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await ReconcileAllActiveSubscriptionsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during reconciliation cycle");
            }
        }

        _logger.LogInformation("ReconciliationWorker stopped");
    }

    private async Task ReconcileAllActiveSubscriptionsAsync(CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var subscriptionRepository = scope.ServiceProvider.GetRequiredService<ISubscriptionRepository>();
        var comparisonOrchestrator = scope.ServiceProvider.GetRequiredService<IComparisonOrchestrator>();
        var pendingChangeRepository = scope.ServiceProvider.GetRequiredService<IPendingChangeRepository>();

        var subscriptions = await subscriptionRepository.GetAllAsync(cancellationToken);
        var candidates = subscriptions
            .Where(s => s.IsActive() && s.Options.AutoCompare)
            .ToList();

        _logger.LogDebug("Found {Count} subscriptions for reconciliation", candidates.Count);

        foreach (var subscription in candidates)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                await ReconcileSubscriptionAsync(
                    subscription,
                    comparisonOrchestrator,
                    pendingChangeRepository,
                    subscriptionRepository,
                    cancellationToken);
            }
            catch (ComparisonInProgressException)
            {
                _logger.LogDebug(
                    "Skipping reconciliation for subscription {SubscriptionId} - comparison already in progress",
                    subscription.Id);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to reconcile subscription {SubscriptionId}",
                    subscription.Id);
            }
        }
    }

    private async Task ReconcileSubscriptionAsync(
        Subscription subscription,
        IComparisonOrchestrator comparisonOrchestrator,
        IPendingChangeRepository pendingChangeRepository,
        ISubscriptionRepository subscriptionRepository,
        CancellationToken cancellationToken)
    {
        // Skip if a comparison was triggered within the last reconciliation interval
        if (subscription.LastComparedAt != null &&
            DateTime.UtcNow - subscription.LastComparedAt.Value < _reconciliationInterval)
        {
            _logger.LogDebug(
                "Skipping reconciliation for subscription {SubscriptionId} - recently compared at {LastComparedAt}",
                subscription.Id, subscription.LastComparedAt);
            return;
        }

        // Add random jitter (0-30s) to avoid thundering herd
        var jitter = TimeSpan.FromSeconds(_random.Next(0, 30));
        await Task.Delay(jitter, cancellationToken);

        _logger.LogInformation(
            "Running reconciliation comparison for subscription {SubscriptionId}",
            subscription.Id);

        // Run full comparison
        await comparisonOrchestrator.RunComparisonAsync(subscription.Id, fullComparison: true, trigger: "reconciliation", cancellationToken);

        // Process accumulated pending changes and mark as processed
        var pendingChanges = await pendingChangeRepository.GetPendingForSubscriptionAsync(
            subscription.Id, cancellationToken);

        foreach (var change in pendingChanges)
        {
            await pendingChangeRepository.MarkAsProcessedAsync(change.Id, cancellationToken);
        }

        if (pendingChanges.Count > 0)
        {
            _logger.LogInformation(
                "Marked {Count} pending changes as processed for subscription {SubscriptionId}",
                pendingChanges.Count, subscription.Id);
        }
    }
}

