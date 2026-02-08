using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SqlSyncService.ChangeDetection;
using SqlSyncService.Configuration;
using SqlSyncService.Domain.Changes;
using SqlSyncService.Domain.Comparisons;
using SqlSyncService.Domain.Subscriptions;
using SqlSyncService.Persistence;
using SqlSyncService.Workers;

namespace SqlSyncService.Tests.Workers;

public class FileWatchingWorkerTests
{
    [Fact]
    public async Task InitializeWatchers_CreatesWatchers_ForActiveSubscriptionsWithCompareOnFileChange()
    {
        // Arrange
        var repo = new InMemorySubscriptionRepository();
        var sub1 = CreateSubscription(compareOnFileChange: true);
        var sub2 = CreateSubscription(compareOnFileChange: false);
        repo.Subscriptions.Add(sub1);
        repo.Subscriptions.Add(sub2);

        var debouncer = new RecordingDebouncer();
        var worker = CreateWorker(repo, debouncer, enabled: true);

        // Act
        await InvokeInitializeWatchersAsync(worker);

        // Assert
        var watchers = GetWatchers(worker);
        Assert.True(watchers.ContainsKey(sub1.Id));
        Assert.False(watchers.ContainsKey(sub2.Id));

        worker.Dispose();
    }

    [Fact]
    public async Task SyncWatchers_RemovesWatchers_WhenSubscriptionNoLongerEligible()
    {
        // Arrange
        var repo = new InMemorySubscriptionRepository();
        var sub = CreateSubscription(compareOnFileChange: true);
        repo.Subscriptions.Add(sub);
        var debouncer = new RecordingDebouncer();
        var worker = CreateWorker(repo, debouncer, enabled: true);

        await InvokeInitializeWatchersAsync(worker);
        var watchersBefore = GetWatchers(worker);
        Assert.True(watchersBefore.ContainsKey(sub.Id));

        // Make subscription no longer eligible
        sub.Options.CompareOnFileChange = false;

        // Act
        await InvokeSyncWatchersAsync(worker);

        // Assert
        var watchersAfter = GetWatchers(worker);
        Assert.False(watchersAfter.ContainsKey(sub.Id));

        worker.Dispose();
    }

    [Fact]
    public void FileChangeEvents_RecordChanges_ViaDebouncer()
    {
        // Arrange
        var repo = new InMemorySubscriptionRepository();
        var sub = CreateSubscription(compareOnFileChange: true);
        repo.Subscriptions.Add(sub);
        var debouncer = new RecordingDebouncer();
        var worker = CreateWorker(repo, debouncer, enabled: true);

        var changedArgs = new FileSystemEventArgs(WatcherChangeTypes.Changed, ".", "changed.sql");
        var renamedArgs = new RenamedEventArgs(WatcherChangeTypes.Renamed, ".", "new.sql", "old.sql");

        // Act
        InvokeOnFileChanged(worker, sub.Id, changedArgs, ChangeType.Modified);
        InvokeOnFileRenamed(worker, sub.Id, renamedArgs);

        // Assert
        Assert.Equal(3, debouncer.RecordedChanges.Count);

        var change = debouncer.RecordedChanges[0];
        Assert.Equal(sub.Id, change.SubscriptionId);
        Assert.Equal(ChangeSource.FileSystem, change.Source);
        Assert.Equal(ChangeType.Modified, change.Type);

        var delete = debouncer.RecordedChanges[1];
        var create = debouncer.RecordedChanges[2];
        Assert.Equal(ChangeType.Deleted, delete.Type);
        Assert.Equal(ChangeType.Created, create.Type);

        worker.Dispose();
    }

    private static Subscription CreateSubscription(bool compareOnFileChange)
    {
        return new Subscription
        {
            Id = Guid.NewGuid(),
            Name = "Test",
            State = SubscriptionState.Active,
            Project = new ProjectFolder { RootPath = "." },
            Options = new ComparisonOptions { CompareOnFileChange = compareOnFileChange }
        };
    }

    private static FileWatchingWorker CreateWorker(InMemorySubscriptionRepository repo, IChangeDebouncer debouncer, bool enabled)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ISubscriptionRepository>(repo);
        var provider = services.BuildServiceProvider();

        var config = Options.Create(new ServiceConfiguration
        {
            Workers = new WorkerSettings
            {
                EnableFileWatching = enabled
            }
        });

        var logger = new LoggerFactory().CreateLogger<FileWatchingWorker>();
        return new FileWatchingWorker(provider, debouncer, config, logger);
    }

    private static async Task InvokeInitializeWatchersAsync(FileWatchingWorker worker)
    {
        var method = typeof(FileWatchingWorker).GetMethod("InitializeWatchersAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var task = (Task)method.Invoke(worker, new object[] { CancellationToken.None })!;
        await task.ConfigureAwait(false);
    }

    private static async Task InvokeSyncWatchersAsync(FileWatchingWorker worker)
    {
        var method = typeof(FileWatchingWorker).GetMethod("SyncWatchersWithActiveSubscriptionsAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var task = (Task)method.Invoke(worker, new object[] { CancellationToken.None })!;
        await task.ConfigureAwait(false);
    }

    private static ConcurrentDictionary<Guid, FileSystemWatcher> GetWatchers(FileWatchingWorker worker)
    {
        var field = typeof(FileWatchingWorker).GetField("_watchers", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (ConcurrentDictionary<Guid, FileSystemWatcher>)field.GetValue(worker)!;
    }

    private static void InvokeOnFileChanged(FileWatchingWorker worker, Guid id, FileSystemEventArgs args, ChangeType type)
    {
        var method = typeof(FileWatchingWorker).GetMethod("OnFileChanged", BindingFlags.NonPublic | BindingFlags.Instance)!;
        method.Invoke(worker, new object[] { id, args, type });
    }

    private static void InvokeOnFileRenamed(FileWatchingWorker worker, Guid id, RenamedEventArgs args)
    {
        var method = typeof(FileWatchingWorker).GetMethod("OnFileRenamed", BindingFlags.NonPublic | BindingFlags.Instance)!;
        method.Invoke(worker, new object[] { id, args });
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

    private sealed class RecordingDebouncer : IChangeDebouncer
    {
        public List<(Guid SubscriptionId, string ObjectIdentifier, ChangeSource Source, ChangeType Type, SqlObjectType? ObjectType)> RecordedChanges { get; } = new();

        public void RecordChange(Guid subscriptionId, string objectIdentifier, ChangeSource source, ChangeType type)
        {
            RecordedChanges.Add((subscriptionId, objectIdentifier, source, type, null));
        }

        public void RecordChange(Guid subscriptionId, string objectIdentifier, ChangeSource source, ChangeType type, SqlObjectType objectType)
        {
            RecordedChanges.Add((subscriptionId, objectIdentifier, source, type, objectType));
        }

        public event EventHandler<PendingChangeBatch>? BatchReady;
    }
}

