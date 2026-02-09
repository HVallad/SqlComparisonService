using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SqlSyncService.Configuration;
using SqlSyncService.Domain.Changes;
using SqlSyncService.Domain.Comparisons;
using SqlSyncService.Domain.Subscriptions;
using SqlSyncService.Persistence;
using SqlSyncService.Services;
using SqlSyncService.Workers;

namespace SqlSyncService.Tests.Workers;

public class ReconciliationWorkerTests
{
    [Fact]
    public async Task ExecuteAsync_RunsFullComparisonForActiveAutoCompareSubscriptions()
    {
        // Arrange
        var repo = new InMemorySubscriptionRepository();
        var pendingRepo = new InMemoryPendingChangeRepository();
        var orchestrator = new RecordingComparisonOrchestrator();

        var active1 = CreateSubscription(autoCompare: true, recentlyCompared: false);
        var active2 = CreateSubscription(autoCompare: true, recentlyCompared: false);
        var inactive = CreateSubscription(autoCompare: true, recentlyCompared: false);
        inactive.State = SubscriptionState.Paused;

        repo.Subscriptions.AddRange(new[] { active1, active2, inactive });

        var worker = CreateWorker(repo, pendingRepo, orchestrator, enabled: true, interval: TimeSpan.FromMinutes(5));
        OverrideRandom(worker);

        // Act
        await InvokeReconcileAllAsync(worker);

        // Assert - Only two active subscriptions should be reconciled
        Assert.Equal(2, orchestrator.RunComparisonCalls.Count);
        Assert.All(orchestrator.RunComparisonCalls, c => Assert.True(c.FullComparison));
        Assert.Contains(orchestrator.RunComparisonCalls, c => c.SubscriptionId == active1.Id);
        Assert.Contains(orchestrator.RunComparisonCalls, c => c.SubscriptionId == active2.Id);
    }

    [Fact]
    public async Task ExecuteAsync_SkipsRecentlyComparedSubscriptions_AndProcessesPendingChanges()
    {
        // Arrange
        var repo = new InMemorySubscriptionRepository();
        var pendingRepo = new InMemoryPendingChangeRepository();
        var orchestrator = new RecordingComparisonOrchestrator();

        var recent = CreateSubscription(autoCompare: true, recentlyCompared: true);
        var stale = CreateSubscription(autoCompare: true, recentlyCompared: false);

        repo.Subscriptions.AddRange(new[] { recent, stale });

        // Add pending changes for stale subscription
        var change1 = new DetectedChange { Id = Guid.NewGuid(), SubscriptionId = stale.Id };
        var change2 = new DetectedChange { Id = Guid.NewGuid(), SubscriptionId = stale.Id };
        pendingRepo.Changes.AddRange(new[] { change1, change2 });

        var worker = CreateWorker(repo, pendingRepo, orchestrator, enabled: true, interval: TimeSpan.FromMinutes(5));
        OverrideRandom(worker);

        // Act
        await InvokeReconcileAllAsync(worker);

        // Assert - Only stale subscription should be reconciled
        Assert.Single(orchestrator.RunComparisonCalls);
        Assert.Equal(stale.Id, orchestrator.RunComparisonCalls[0].SubscriptionId);

        // All pending changes for stale subscription should be marked processed
        Assert.Equal(2, pendingRepo.ProcessedIds.Count);
        Assert.Contains(change1.Id, pendingRepo.ProcessedIds);
        Assert.Contains(change2.Id, pendingRepo.ProcessedIds);
    }

    private static Subscription CreateSubscription(bool autoCompare, bool recentlyCompared)
    {
        return new Subscription
        {
            Id = Guid.NewGuid(),
            Name = "Test",
            State = SubscriptionState.Active,
            Options = new ComparisonOptions { AutoCompare = autoCompare },
            LastComparedAt = recentlyCompared ? DateTime.UtcNow.AddMinutes(-1) : null
        };
    }

    private static ReconciliationWorker CreateWorker(
        InMemorySubscriptionRepository repo,
        InMemoryPendingChangeRepository pendingRepo,
        RecordingComparisonOrchestrator orchestrator,
        bool enabled,
        TimeSpan interval)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ISubscriptionRepository>(repo);
        services.AddSingleton<IPendingChangeRepository>(pendingRepo);
        services.AddSingleton<IComparisonOrchestrator>(orchestrator);
        var provider = services.BuildServiceProvider();

        var config = Options.Create(new ServiceConfiguration
        {
            Monitoring = new MonitoringSettings
            {
                FullReconciliationInterval = interval
            },
            Workers = new WorkerSettings
            {
                EnableReconciliation = enabled
            }
        });

        var logger = new LoggerFactory().CreateLogger<ReconciliationWorker>();
        return new ReconciliationWorker(provider, config, logger);
    }

    private static async Task InvokeReconcileAllAsync(ReconciliationWorker worker)
    {
        var method = typeof(ReconciliationWorker).GetMethod("ReconcileAllActiveSubscriptionsAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var task = (Task)method.Invoke(worker, new object[] { CancellationToken.None })!;
        await task.ConfigureAwait(false);
    }

    private static void OverrideRandom(ReconciliationWorker worker)
    {
        var field = typeof(ReconciliationWorker).GetField("_random", BindingFlags.NonPublic | BindingFlags.Instance)!;
        field.SetValue(worker, new ZeroRandom());
    }

    private sealed class InMemorySubscriptionRepository : ISubscriptionRepository
    {
        public List<Subscription> Subscriptions { get; } = new();

        public Task<Subscription?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<Subscription?>(Subscriptions.FirstOrDefault(s => s.Id == id));

        public Task<IReadOnlyList<Subscription>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Subscription>>(Subscriptions.ToList());

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

    private sealed class InMemoryPendingChangeRepository : IPendingChangeRepository
    {
        public List<DetectedChange> Changes { get; } = new();
        public List<Guid> ProcessedIds { get; } = new();

        public Task AddAsync(DetectedChange change, CancellationToken cancellationToken = default)
        {
            Changes.Add(change);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<DetectedChange>> GetPendingForSubscriptionAsync(Guid subscriptionId, CancellationToken cancellationToken = default)
        {
            var pending = Changes.Where(c => c.SubscriptionId == subscriptionId).ToList();
            return Task.FromResult<IReadOnlyList<DetectedChange>>(pending);
        }

        public Task MarkAsProcessedAsync(Guid id, CancellationToken cancellationToken = default)
        {
            ProcessedIds.Add(id);
            var change = Changes.FirstOrDefault(c => c.Id == id);
            if (change != null)
            {
                change.IsProcessed = true;
                change.ProcessedAt = DateTime.UtcNow;
            }
            return Task.CompletedTask;
        }

        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
        {
            var removed = Changes.RemoveAll(c => c.Id == id);
            return Task.FromResult(removed > 0);
        }

        public Task<int> DeleteProcessedOlderThanAsync(DateTime threshold, CancellationToken cancellationToken = default)
        {
            var before = Changes.Count;
            Changes.RemoveAll(c => c.IsProcessed && c.ProcessedAt <= threshold);
            var removed = before - Changes.Count;
            return Task.FromResult(removed);
        }
    }

    private sealed class RecordingComparisonOrchestrator : IComparisonOrchestrator
    {
        public List<(Guid SubscriptionId, bool FullComparison)> RunComparisonCalls { get; } = new();

        public Task<ComparisonResult> RunComparisonAsync(Guid subscriptionId, bool fullComparison, string trigger, CancellationToken cancellationToken = default)
        {
            RunComparisonCalls.Add((subscriptionId, fullComparison));
            return Task.FromResult(new ComparisonResult
            {
                Id = Guid.NewGuid(),
                SubscriptionId = subscriptionId,
                ComparedAt = DateTime.UtcNow,
                Status = ComparisonStatus.Synchronized
            });
        }

        public Task<SingleObjectComparisonResult> CompareObjectAsync(Guid subscriptionId, string schemaName, string objectName, SqlObjectType objectType, string trigger, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public Task<ComparisonResult> CompareObjectsAsync(Guid subscriptionId, IEnumerable<ObjectIdentifier> changedObjects, string trigger, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }
    }

    private sealed class ZeroRandom : Random
    {
        public override int Next(int minValue, int maxValue) => minValue;
    }
}

