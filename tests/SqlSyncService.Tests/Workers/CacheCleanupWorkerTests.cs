using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SqlSyncService.Configuration;
using SqlSyncService.Domain.Caching;
using SqlSyncService.Domain.Changes;
using SqlSyncService.Domain.Comparisons;
using SqlSyncService.Domain.Subscriptions;
using SqlSyncService.Persistence;
using SqlSyncService.Workers;

namespace SqlSyncService.Tests.Workers;

public class CacheCleanupWorkerTests
{
    [Fact]
    public async Task CleanupAsync_DeletesSnapshotsOlderThanRetentionPeriod()
    {
        // Arrange
        var snapshotRepo = new InMemorySchemaSnapshotRepository();
        var subscriptionId = Guid.NewGuid();
        
        // Add old snapshot (8 days old, retention is 7 days)
        snapshotRepo.Snapshots.Add(new SchemaSnapshot
        {
            Id = Guid.NewGuid(),
            SubscriptionId = subscriptionId,
            CapturedAt = DateTime.UtcNow.AddDays(-8)
        });
        
        // Add recent snapshot
        snapshotRepo.Snapshots.Add(new SchemaSnapshot
        {
            Id = Guid.NewGuid(),
            SubscriptionId = subscriptionId,
            CapturedAt = DateTime.UtcNow.AddDays(-1)
        });

        var services = CreateServices(snapshotRepo: snapshotRepo);
        var worker = CreateWorker(services);

        // Act
        await InvokeCleanupAsync(worker);

        // Assert
        Assert.Single(snapshotRepo.Snapshots);
        Assert.True(snapshotRepo.Snapshots[0].CapturedAt > DateTime.UtcNow.AddDays(-7));
    }

    [Fact]
    public async Task CleanupAsync_DeletesExcessSnapshotsPerSubscription()
    {
        // Arrange
        var snapshotRepo = new InMemorySchemaSnapshotRepository();
        var subscriptionId = Guid.NewGuid();
        
        // Add 15 snapshots (max is 10)
        for (int i = 0; i < 15; i++)
        {
            snapshotRepo.Snapshots.Add(new SchemaSnapshot
            {
                Id = Guid.NewGuid(),
                SubscriptionId = subscriptionId,
                CapturedAt = DateTime.UtcNow.AddHours(-i)
            });
        }

        var subscriptionRepo = new InMemorySubscriptionRepository();
        subscriptionRepo.Subscriptions.Add(new Subscription
        {
            Id = subscriptionId,
            Name = "Test",
            Database = new DatabaseConnection(),
            Project = new ProjectFolder()
        });

        var services = CreateServices(snapshotRepo: snapshotRepo, subscriptionRepo: subscriptionRepo);
        var worker = CreateWorker(services);

        // Act
        await InvokeCleanupAsync(worker);

        // Assert - Should keep only 10 most recent
        Assert.Equal(10, snapshotRepo.Snapshots.Count);
    }

    [Fact]
    public async Task CleanupAsync_DeletesOldComparisonHistory()
    {
        // Arrange
        var historyRepo = new InMemoryComparisonHistoryRepository();
        var subscriptionId = Guid.NewGuid();
        
        // Add old history entry (31 days old, retention is 30 days)
        historyRepo.Results.Add(new ComparisonResult
        {
            Id = Guid.NewGuid(),
            SubscriptionId = subscriptionId,
            ComparedAt = DateTime.UtcNow.AddDays(-31)
        });
        
        // Add recent history entry
        historyRepo.Results.Add(new ComparisonResult
        {
            Id = Guid.NewGuid(),
            SubscriptionId = subscriptionId,
            ComparedAt = DateTime.UtcNow.AddDays(-1)
        });

        var services = CreateServices(historyRepo: historyRepo);
        var worker = CreateWorker(services);

        // Act
        await InvokeCleanupAsync(worker);

        // Assert
        Assert.Single(historyRepo.Results);
    }

    [Fact]
    public async Task CleanupAsync_DeletesProcessedPendingChanges()
    {
        // Arrange
        var pendingChangeRepo = new InMemoryPendingChangeRepository();
        var subscriptionId = Guid.NewGuid();
        
        // Add old processed change (2 days old, retention is 1 day)
        pendingChangeRepo.Changes.Add(new DetectedChange
        {
            Id = Guid.NewGuid(),
            SubscriptionId = subscriptionId,
            DetectedAt = DateTime.UtcNow.AddDays(-2),
            IsProcessed = true,
            ProcessedAt = DateTime.UtcNow.AddDays(-2)
        });
        
        // Add recent unprocessed change
        pendingChangeRepo.Changes.Add(new DetectedChange
        {
            Id = Guid.NewGuid(),
            SubscriptionId = subscriptionId,
            DetectedAt = DateTime.UtcNow,
            IsProcessed = false
        });

        var services = CreateServices(pendingChangeRepo: pendingChangeRepo);
        var worker = CreateWorker(services);

        // Act
        await InvokeCleanupAsync(worker);

        // Assert - Old processed change should be deleted, unprocessed should remain
        Assert.Single(pendingChangeRepo.Changes);
        Assert.False(pendingChangeRepo.Changes[0].IsProcessed);
    }

    [Fact]
    public async Task CleanupAsync_DoesNotRun_WhenDisabled()
    {
        // Arrange
        var snapshotRepo = new InMemorySchemaSnapshotRepository();
        snapshotRepo.Snapshots.Add(new SchemaSnapshot
        {
            Id = Guid.NewGuid(),
            SubscriptionId = Guid.NewGuid(),
            CapturedAt = DateTime.UtcNow.AddDays(-30)
        });

        var services = CreateServices(snapshotRepo: snapshotRepo);
        var config = CreateConfig(enabled: false);
        var worker = new CacheCleanupWorker(services, config, CreateLogger());

        // Act - Worker should exit immediately when disabled
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await worker.StartAsync(cts.Token);
        await worker.StopAsync(CancellationToken.None);

        // Assert - Snapshots should not be touched
        Assert.Single(snapshotRepo.Snapshots);
    }

    private static ServiceProvider CreateServices(
        InMemorySchemaSnapshotRepository? snapshotRepo = null,
        InMemoryComparisonHistoryRepository? historyRepo = null,
        InMemoryPendingChangeRepository? pendingChangeRepo = null,
        InMemorySubscriptionRepository? subscriptionRepo = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ISchemaSnapshotRepository>(snapshotRepo ?? new InMemorySchemaSnapshotRepository());
        services.AddSingleton<IComparisonHistoryRepository>(historyRepo ?? new InMemoryComparisonHistoryRepository());
        services.AddSingleton<IPendingChangeRepository>(pendingChangeRepo ?? new InMemoryPendingChangeRepository());
        services.AddSingleton<ISubscriptionRepository>(subscriptionRepo ?? new InMemorySubscriptionRepository());
        services.AddSingleton<LiteDbContext>(_ => new MockLiteDbContext());
        return services.BuildServiceProvider();
    }

    private static CacheCleanupWorker CreateWorker(IServiceProvider services)
    {
        return new CacheCleanupWorker(services, CreateConfig(), CreateLogger());
    }

    private static IOptions<ServiceConfiguration> CreateConfig(bool enabled = true)
    {
        return Options.Create(new ServiceConfiguration
        {
            Cache = new CacheSettings
            {
                SnapshotRetention = TimeSpan.FromDays(7),
                MaxCachedSnapshots = 10,
                ComparisonHistoryRetention = TimeSpan.FromDays(30),
                PendingChangeRetention = TimeSpan.FromDays(1)
            },
            Workers = new WorkerSettings
            {
                EnableCacheCleanup = enabled,
                CacheCleanupInterval = TimeSpan.FromHours(1)
            }
        });
    }

    private static ILogger<CacheCleanupWorker> CreateLogger()
    {
        return new LoggerFactory().CreateLogger<CacheCleanupWorker>();
    }

    private static async Task InvokeCleanupAsync(CacheCleanupWorker worker)
    {
        // Use reflection to invoke the private CleanupAsync method
        var method = typeof(CacheCleanupWorker).GetMethod("CleanupAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var task = (Task)method!.Invoke(worker, new object[] { CancellationToken.None })!;
        await task;
    }

    private sealed class InMemorySchemaSnapshotRepository : ISchemaSnapshotRepository
    {
        public List<SchemaSnapshot> Snapshots { get; } = new();

        public Task AddAsync(SchemaSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            Snapshots.Add(snapshot);
            return Task.CompletedTask;
        }

        public Task<SchemaSnapshot?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<SchemaSnapshot?>(Snapshots.FirstOrDefault(s => s.Id == id));

        public Task<IReadOnlyList<SchemaSnapshot>> GetBySubscriptionAsync(Guid subscriptionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SchemaSnapshot>>(Snapshots.Where(s => s.SubscriptionId == subscriptionId).ToList());

        public Task<SchemaSnapshot?> GetLatestForSubscriptionAsync(Guid subscriptionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<SchemaSnapshot?>(Snapshots.Where(s => s.SubscriptionId == subscriptionId).OrderByDescending(s => s.CapturedAt).FirstOrDefault());

        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
        {
            Snapshots.RemoveAll(s => s.Id == id);
            return Task.FromResult(true);
        }

        public Task DeleteForSubscriptionAsync(Guid subscriptionId, CancellationToken cancellationToken = default)
        {
            Snapshots.RemoveAll(s => s.SubscriptionId == subscriptionId);
            return Task.CompletedTask;
        }

        public Task<int> DeleteOlderThanAsync(DateTime cutoffDate, CancellationToken cancellationToken = default)
        {
            var before = Snapshots.Count;
            Snapshots.RemoveAll(s => s.CapturedAt < cutoffDate);
            return Task.FromResult(before - Snapshots.Count);
        }

        public Task<int> DeleteExcessForSubscriptionAsync(Guid subscriptionId, int maxCount, CancellationToken cancellationToken = default)
        {
            var forSubscription = Snapshots.Where(s => s.SubscriptionId == subscriptionId).OrderByDescending(s => s.CapturedAt).ToList();
            if (forSubscription.Count <= maxCount) return Task.FromResult(0);
            var toRemove = forSubscription.Skip(maxCount).ToList();
            foreach (var item in toRemove) Snapshots.Remove(item);
            return Task.FromResult(toRemove.Count);
        }
    }

    private sealed class InMemoryComparisonHistoryRepository : IComparisonHistoryRepository
    {
        public List<ComparisonResult> Results { get; } = new();

        public Task AddAsync(ComparisonResult result, CancellationToken cancellationToken = default)
        {
            Results.Add(result);
            return Task.CompletedTask;
        }

        public Task<ComparisonResult?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<ComparisonResult?>(Results.FirstOrDefault(r => r.Id == id));

        public Task<IReadOnlyList<ComparisonResult>> GetBySubscriptionAsync(Guid subscriptionId, int? maxCount = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ComparisonResult>>(Results.Where(r => r.SubscriptionId == subscriptionId).Take(maxCount ?? int.MaxValue).ToList());

        public Task<int> DeleteBySubscriptionAsync(Guid subscriptionId, CancellationToken cancellationToken = default)
        {
            var before = Results.Count;
            Results.RemoveAll(r => r.SubscriptionId == subscriptionId);
            return Task.FromResult(before - Results.Count);
        }

        public Task<int> DeleteOlderThanAsync(DateTime cutoffDate, CancellationToken cancellationToken = default)
        {
            var before = Results.Count;
            Results.RemoveAll(r => r.ComparedAt < cutoffDate);
            return Task.FromResult(before - Results.Count);
        }
    }

    private sealed class InMemoryPendingChangeRepository : IPendingChangeRepository
    {
        public List<DetectedChange> Changes { get; } = new();

        public Task AddAsync(DetectedChange change, CancellationToken cancellationToken = default)
        {
            Changes.Add(change);
            return Task.CompletedTask;
        }

        public Task MarkAsProcessedAsync(Guid id, CancellationToken cancellationToken = default)
        {
            var change = Changes.FirstOrDefault(c => c.Id == id);
            if (change != null) { change.IsProcessed = true; change.ProcessedAt = DateTime.UtcNow; }
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<DetectedChange>> GetPendingForSubscriptionAsync(Guid subscriptionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DetectedChange>>(Changes.Where(c => c.SubscriptionId == subscriptionId && !c.IsProcessed).ToList());

        public Task<int> DeleteProcessedOlderThanAsync(DateTime cutoffDate, CancellationToken cancellationToken = default)
        {
            var before = Changes.Count;
            Changes.RemoveAll(c => c.IsProcessed && c.ProcessedAt < cutoffDate);
            return Task.FromResult(before - Changes.Count);
        }

        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
        {
            Changes.RemoveAll(c => c.Id == id);
            return Task.FromResult(true);
        }
    }

    private sealed class InMemorySubscriptionRepository : ISubscriptionRepository
    {
        public List<Subscription> Subscriptions { get; } = new();

        public Task<Subscription?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<Subscription?>(Subscriptions.FirstOrDefault(s => s.Id == id));

        public Task<IReadOnlyList<Subscription>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Subscription>>(Subscriptions);

        public Task<Subscription?> GetByNameAsync(string name, CancellationToken cancellationToken = default) =>
            Task.FromResult<Subscription?>(Subscriptions.FirstOrDefault(s => s.Name == name));

        public Task AddAsync(Subscription subscription, CancellationToken cancellationToken = default)
        {
            Subscriptions.Add(subscription);
            return Task.CompletedTask;
        }

        public Task UpdateAsync(Subscription subscription, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
        {
            Subscriptions.RemoveAll(s => s.Id == id);
            return Task.FromResult(true);
        }
    }

    private sealed class MockLiteDbContext : LiteDbContext
    {
        public MockLiteDbContext() : base(new LiteDB.LiteDatabase(":memory:")) { }
    }
}

