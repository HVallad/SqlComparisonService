using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using SqlSyncService.ChangeDetection;
using SqlSyncService.Configuration;
using SqlSyncService.Domain.Changes;
using SqlSyncService.Domain.Subscriptions;
using SqlSyncService.Persistence;

namespace SqlSyncService.Workers;

/// <summary>
/// Background worker that monitors SQL project folders for file changes in real-time.
/// Uses FileSystemWatcher per active subscription and aggregates changes via debouncer.
/// </summary>
public sealed class FileWatchingWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<FileWatchingWorker> _logger;
    private readonly IChangeDebouncer _debouncer;
    private readonly bool _enabled;

    // One watcher per active subscription
    private readonly ConcurrentDictionary<Guid, FileSystemWatcher> _watchers = new();

    public FileWatchingWorker(
        IServiceProvider serviceProvider,
        IChangeDebouncer debouncer,
        IOptions<ServiceConfiguration> config,
        ILogger<FileWatchingWorker> logger)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _debouncer = debouncer ?? throw new ArgumentNullException(nameof(debouncer));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _enabled = config.Value.Workers.EnableFileWatching;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            _logger.LogInformation("FileWatchingWorker is disabled via configuration");
            return;
        }

        _logger.LogInformation("FileWatchingWorker starting");

        await InitializeWatchersAsync(stoppingToken);

        // Keep service running and sync watchers periodically
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await SyncWatchersWithActiveSubscriptionsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error syncing file watchers");
            }
        }

        // Cleanup watchers on shutdown
        CleanupAllWatchers();
        _logger.LogInformation("FileWatchingWorker stopped");
    }

    private async Task InitializeWatchersAsync(CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var subscriptionRepository = scope.ServiceProvider.GetRequiredService<ISubscriptionRepository>();

        var subscriptions = await subscriptionRepository.GetAllAsync(cancellationToken);
        foreach (var subscription in subscriptions)
        {
            if (ShouldWatch(subscription))
            {
                TryCreateWatcher(subscription);
            }
        }

        _logger.LogInformation("Initialized {Count} file watchers", _watchers.Count);
    }

    private async Task SyncWatchersWithActiveSubscriptionsAsync(CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var subscriptionRepository = scope.ServiceProvider.GetRequiredService<ISubscriptionRepository>();

        var subscriptions = await subscriptionRepository.GetAllAsync(cancellationToken);
        var activeSubscriptionIds = subscriptions
            .Where(ShouldWatch)
            .Select(s => s.Id)
            .ToHashSet();

        // Remove watchers for subscriptions that are no longer active/watching
        var toRemove = _watchers.Keys.Where(id => !activeSubscriptionIds.Contains(id)).ToList();
        foreach (var id in toRemove)
        {
            RemoveWatcher(id);
        }

        // Add watchers for new active subscriptions
        foreach (var subscription in subscriptions.Where(ShouldWatch))
        {
            if (!_watchers.ContainsKey(subscription.Id))
            {
                TryCreateWatcher(subscription);
            }
        }
    }

    private static bool ShouldWatch(Subscription subscription)
    {
        return subscription.IsActive() && subscription.Options.CompareOnFileChange;
    }

    private void TryCreateWatcher(Subscription subscription)
    {
        try
        {
            var watcher = CreateWatcher(subscription);
            if (_watchers.TryAdd(subscription.Id, watcher))
            {
                _logger.LogDebug(
                    "Created file watcher for subscription {SubscriptionId} at {Path}",
                    subscription.Id, subscription.Project.RootPath);
            }
            else
            {
                watcher.Dispose();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to create file watcher for subscription {SubscriptionId} at {Path}",
                subscription.Id, subscription.Project.RootPath);
        }
    }

    private FileSystemWatcher CreateWatcher(Subscription subscription)
    {
        var watcher = new FileSystemWatcher(subscription.Project.RootPath)
        {
            Filter = "*.sql",
            IncludeSubdirectories = true,
            EnableRaisingEvents = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName
        };

        var subscriptionId = subscription.Id;
        watcher.Changed += (s, e) => OnFileChanged(subscriptionId, e, ChangeType.Modified);
        watcher.Created += (s, e) => OnFileChanged(subscriptionId, e, ChangeType.Created);
        watcher.Deleted += (s, e) => OnFileChanged(subscriptionId, e, ChangeType.Deleted);
        watcher.Renamed += (s, e) => OnFileRenamed(subscriptionId, e);
        watcher.Error += (s, e) => OnWatcherError(subscriptionId, e);

        return watcher;
    }

    private void OnFileChanged(Guid subscriptionId, FileSystemEventArgs e, ChangeType changeType)
    {
        try
        {
            // Use relative path as object identifier
            var objectIdentifier = e.FullPath;
            _debouncer.RecordChange(subscriptionId, objectIdentifier, ChangeSource.FileSystem, changeType);
            _logger.LogDebug(
                "File {ChangeType}: {Path} for subscription {SubscriptionId}",
                changeType, e.FullPath, subscriptionId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error handling file change event for subscription {SubscriptionId}", subscriptionId);
        }
    }

    private void OnFileRenamed(Guid subscriptionId, RenamedEventArgs e)
    {
        try
        {
            // Record as delete for old path and create for new path
            _debouncer.RecordChange(subscriptionId, e.OldFullPath, ChangeSource.FileSystem, ChangeType.Deleted);
            _debouncer.RecordChange(subscriptionId, e.FullPath, ChangeSource.FileSystem, ChangeType.Created);
            _logger.LogDebug(
                "File renamed: {OldPath} -> {NewPath} for subscription {SubscriptionId}",
                e.OldFullPath, e.FullPath, subscriptionId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error handling file rename event for subscription {SubscriptionId}", subscriptionId);
        }
    }

    private void OnWatcherError(Guid subscriptionId, ErrorEventArgs e)
    {
        var ex = e.GetException();
        _logger.LogError(ex, "FileSystemWatcher error for subscription {SubscriptionId}", subscriptionId);

        // Try to recreate the watcher
        if (_watchers.TryRemove(subscriptionId, out var watcher))
        {
            watcher.Dispose();
            _logger.LogInformation("Removed failed watcher for subscription {SubscriptionId}", subscriptionId);
        }
    }

    private void RemoveWatcher(Guid subscriptionId)
    {
        if (_watchers.TryRemove(subscriptionId, out var watcher))
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
            _logger.LogDebug("Removed file watcher for subscription {SubscriptionId}", subscriptionId);
        }
    }

    private void CleanupAllWatchers()
    {
        foreach (var subscriptionId in _watchers.Keys.ToList())
        {
            RemoveWatcher(subscriptionId);
        }
    }

    public override void Dispose()
    {
        CleanupAllWatchers();
        base.Dispose();
    }
}

