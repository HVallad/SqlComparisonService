using Microsoft.Extensions.Options;
using SqlSyncService.Configuration;
using SqlSyncService.Persistence;

namespace SqlSyncService.Workers;

/// <summary>
/// Background worker that enforces retention policies for snapshots, comparison history,
/// and pending changes. Runs periodically to clean up old data and compact the database.
/// </summary>
public sealed class CacheCleanupWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<CacheCleanupWorker> _logger;
    private readonly CacheSettings _cacheSettings;
    private readonly TimeSpan _cleanupInterval;
    private readonly bool _enabled;

    public CacheCleanupWorker(
        IServiceProvider serviceProvider,
        IOptions<ServiceConfiguration> config,
        ILogger<CacheCleanupWorker> logger)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _cacheSettings = config.Value.Cache;
        _cleanupInterval = config.Value.Workers.CacheCleanupInterval;
        _enabled = config.Value.Workers.EnableCacheCleanup;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            _logger.LogInformation("CacheCleanupWorker is disabled via configuration");
            return;
        }

        _logger.LogInformation("CacheCleanupWorker started with interval {Interval}", _cleanupInterval);

        using var timer = new PeriodicTimer(_cleanupInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await CleanupAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during cache cleanup cycle");
            }
        }

        _logger.LogInformation("CacheCleanupWorker stopped");
    }

    private async Task CleanupAsync(CancellationToken cancellationToken)
    {
        _logger.LogDebug("Starting cache cleanup cycle");
        var totalDeleted = 0;

        using var scope = _serviceProvider.CreateScope();
        var snapshotRepository = scope.ServiceProvider.GetRequiredService<ISchemaSnapshotRepository>();
        var historyRepository = scope.ServiceProvider.GetRequiredService<IComparisonHistoryRepository>();
        var pendingChangeRepository = scope.ServiceProvider.GetRequiredService<IPendingChangeRepository>();
        var subscriptionRepository = scope.ServiceProvider.GetRequiredService<ISubscriptionRepository>();
        var dbContext = scope.ServiceProvider.GetRequiredService<LiteDbContext>();

        // 1. Delete snapshots older than retention period
        var snapshotCutoff = DateTime.UtcNow - _cacheSettings.SnapshotRetention;
        var snapshotsDeleted = await snapshotRepository.DeleteOlderThanAsync(snapshotCutoff, cancellationToken);
        totalDeleted += snapshotsDeleted;
        if (snapshotsDeleted > 0)
        {
            _logger.LogInformation("Deleted {Count} snapshots older than {Cutoff}", snapshotsDeleted, snapshotCutoff);
        }

        // 2. Delete excess snapshots per subscription (keep only MaxCachedSnapshots)
        var subscriptions = await subscriptionRepository.GetAllAsync(cancellationToken);
        foreach (var subscription in subscriptions)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            var excessDeleted = await snapshotRepository.DeleteExcessForSubscriptionAsync(
                subscription.Id, _cacheSettings.MaxCachedSnapshots, cancellationToken);
            totalDeleted += excessDeleted;
            if (excessDeleted > 0)
            {
                _logger.LogDebug(
                    "Deleted {Count} excess snapshots for subscription {SubscriptionId}",
                    excessDeleted, subscription.Id);
            }
        }

        // 3. Delete comparison history older than retention period
        var historyCutoff = DateTime.UtcNow - _cacheSettings.ComparisonHistoryRetention;
        var historyDeleted = await historyRepository.DeleteOlderThanAsync(historyCutoff, cancellationToken);
        totalDeleted += historyDeleted;
        if (historyDeleted > 0)
        {
            _logger.LogInformation(
                "Deleted {Count} comparison history entries older than {Cutoff}",
                historyDeleted, historyCutoff);
        }

        // 4. Delete processed pending changes older than retention period
        var pendingCutoff = DateTime.UtcNow - _cacheSettings.PendingChangeRetention;
        var pendingDeleted = await pendingChangeRepository.DeleteProcessedOlderThanAsync(
            pendingCutoff, cancellationToken);
        totalDeleted += pendingDeleted;
        if (pendingDeleted > 0)
        {
            _logger.LogInformation(
                "Deleted {Count} processed pending changes older than {Cutoff}",
                pendingDeleted, pendingCutoff);
        }

        // 5. Compact LiteDB to reclaim space if we deleted anything
        if (totalDeleted > 0)
        {
            try
            {
                var bytesReclaimed = dbContext.Compact();
                _logger.LogInformation(
                    "Cache cleanup complete: {TotalDeleted} items deleted, {BytesReclaimed} bytes reclaimed",
                    totalDeleted, bytesReclaimed);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to compact database after cleanup");
            }
        }
        else
        {
            _logger.LogDebug("Cache cleanup complete: no items to delete");
        }
    }
}

