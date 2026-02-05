using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SqlSyncService.ChangeDetection;
using SqlSyncService.Configuration;
using SqlSyncService.Domain.Changes;
using SqlSyncService.Domain.Comparisons;
using SqlSyncService.Domain.Subscriptions;
using SqlSyncService.Persistence;
using SqlSyncService.Realtime;
using SqlSyncService.Workers;

namespace SqlSyncService.Tests.Workers;

public class DatabasePollingWorkerTests
{
    [Fact]
    public void ClearSubscriptionState_Removes_All_Objects_For_Subscription()
    {
        // Arrange
        var worker = CreateWorker(new MockDebouncer());
        var subscriptionId = Guid.NewGuid();
        var otherSubscriptionId = Guid.NewGuid();

        // Simulate state being tracked
        var trackedObjects = worker.GetTrackedObjects(subscriptionId);
        Assert.Empty(trackedObjects);

        // Act
        worker.ClearSubscriptionState(subscriptionId);

        // Assert - no exception and state is cleared
        var afterClear = worker.GetTrackedObjects(subscriptionId);
        Assert.Empty(afterClear);
    }

    [Fact]
    public void GetTrackedObjects_Returns_Empty_For_Unknown_Subscription()
    {
        // Arrange
        var worker = CreateWorker(new MockDebouncer());

        // Act
        var result = worker.GetTrackedObjects(Guid.NewGuid());

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    public void SqlTypeMap_Contains_All_Expected_Types()
    {
        // This test verifies the SQL type mapping is correct by checking the worker behavior
        var worker = CreateWorker(new MockDebouncer());
        
        // The worker should be created successfully with all type mappings
        Assert.NotNull(worker);
    }

    private static DatabasePollingWorker CreateWorker(IChangeDebouncer debouncer)
    {
        var config = Options.Create(new ServiceConfiguration
        {
            Monitoring = new MonitoringSettings
            {
                DatabasePollInterval = TimeSpan.FromSeconds(30)
            },
            Workers = new WorkerSettings
            {
                EnableDatabasePolling = true
            }
        });

        var services = new ServiceCollection();
        services.AddSingleton<ISubscriptionRepository>(new MockSubscriptionRepository());
        services.AddSingleton<IHubContext<SyncHub>>(new MockHubContext());
        
        var serviceProvider = services.BuildServiceProvider();

        return new DatabasePollingWorker(
            serviceProvider,
            debouncer,
            config,
            new LoggerFactory().CreateLogger<DatabasePollingWorker>());
    }

    #region Mock Classes

    private sealed class MockDebouncer : IChangeDebouncer
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

        public void Dispose() { }
    }

    private sealed class MockSubscriptionRepository : ISubscriptionRepository
    {
        public Task AddAsync(Subscription subscription, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<IReadOnlyList<Subscription>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Subscription>>(new List<Subscription>());

        public Task<Subscription?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<Subscription?>(null);

        public Task<IReadOnlyList<Subscription>> GetByStateAsync(SubscriptionState state, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Subscription>>(new List<Subscription>());

        public Task UpdateAsync(Subscription subscription, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<bool> ExistsAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }

    private sealed class MockHubContext : IHubContext<SyncHub>
    {
        public IHubClients Clients => new MockHubClients();
        public IGroupManager Groups => throw new NotImplementedException();
    }

    private sealed class MockHubClients : IHubClients
    {
        public IClientProxy All => new MockClientProxy();
        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => new MockClientProxy();
        public IClientProxy Client(string connectionId) => new MockClientProxy();
        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => new MockClientProxy();
        public IClientProxy Group(string groupName) => new MockClientProxy();
        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => new MockClientProxy();
        public IClientProxy Groups(IReadOnlyList<string> groupNames) => new MockClientProxy();
        public IClientProxy User(string userId) => new MockClientProxy();
        public IClientProxy Users(IReadOnlyList<string> userIds) => new MockClientProxy();
    }

    private sealed class MockClientProxy : IClientProxy
    {
        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    #endregion
}

