using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SqlSyncService.Configuration;
using SqlSyncService.Contracts;
using SqlSyncService.Contracts.Connections;
using SqlSyncService.Contracts.Folders;
using SqlSyncService.Domain.Comparisons;
using SqlSyncService.Domain.Subscriptions;
using SqlSyncService.Persistence;
using SqlSyncService.Realtime;
using SqlSyncService.Services;
using SqlSyncService.Workers;

namespace SqlSyncService.Tests.Workers;

public class HealthCheckWorkerTests
{
    [Fact]
    public async Task ExecuteAsync_UpdatesSubscriptionHealthToHealthy_WhenAllChecksPass()
    {
        // Arrange
        var subscriptionId = Guid.NewGuid();
        var subscription = CreateSubscription(subscriptionId);
        var subscriptionRepo = new MockSubscriptionRepository(subscription);
        var connectionTester = new MockConnectionTester(success: true);
        var folderValidator = new MockFolderValidator(exists: true, sqlFileCount: 5);

        var services = CreateServices(subscriptionRepo, connectionTester, folderValidator);
        var worker = CreateWorker(services);

        // Act
        await InvokeCheckAllSubscriptionsAsync(worker);

        // Assert
        Assert.Equal(HealthStatus.Healthy, subscription.Health.OverallStatus);
        Assert.True(subscription.Health.DatabaseConnectable);
        Assert.True(subscription.Health.FolderAccessible);
        Assert.True(subscription.Health.SqlFilesPresent);
        Assert.Null(subscription.Health.LastError);
    }

    [Fact]
    public async Task ExecuteAsync_MarksUnhealthy_OnDatabaseConnectionFailure()
    {
        // Arrange
        var subscriptionId = Guid.NewGuid();
        var subscription = CreateSubscription(subscriptionId);
        var subscriptionRepo = new MockSubscriptionRepository(subscription);
        var connectionTester = new MockConnectionTester(success: false, error: "Connection refused");
        var folderValidator = new MockFolderValidator(exists: true, sqlFileCount: 5);

        var services = CreateServices(subscriptionRepo, connectionTester, folderValidator);
        var worker = CreateWorker(services);

        // Act
        await InvokeCheckAllSubscriptionsAsync(worker);

        // Assert
        Assert.Equal(HealthStatus.Unhealthy, subscription.Health.OverallStatus);
        Assert.False(subscription.Health.DatabaseConnectable);
        Assert.Contains("Connection refused", subscription.Health.LastError);
    }

    [Fact]
    public async Task ExecuteAsync_MarksUnhealthy_OnFolderAccessFailure()
    {
        // Arrange
        var subscriptionId = Guid.NewGuid();
        var subscription = CreateSubscription(subscriptionId);
        var subscriptionRepo = new MockSubscriptionRepository(subscription);
        var connectionTester = new MockConnectionTester(success: true);
        var folderValidator = new MockFolderValidator(exists: false, sqlFileCount: 0);

        var services = CreateServices(subscriptionRepo, connectionTester, folderValidator);
        var worker = CreateWorker(services);

        // Act
        await InvokeCheckAllSubscriptionsAsync(worker);

        // Assert
        Assert.Equal(HealthStatus.Unhealthy, subscription.Health.OverallStatus);
        Assert.False(subscription.Health.FolderAccessible);
    }

    [Fact]
    public async Task ExecuteAsync_MarksDegraded_WhenFolderAccessibleButNoSqlFiles()
    {
        // Arrange
        var subscriptionId = Guid.NewGuid();
        var subscription = CreateSubscription(subscriptionId);
        var subscriptionRepo = new MockSubscriptionRepository(subscription);
        var connectionTester = new MockConnectionTester(success: true);
        var folderValidator = new MockFolderValidator(exists: true, sqlFileCount: 0);

        var services = CreateServices(subscriptionRepo, connectionTester, folderValidator);
        var worker = CreateWorker(services);

        // Act
        await InvokeCheckAllSubscriptionsAsync(worker);

        // Assert
        Assert.Equal(HealthStatus.Degraded, subscription.Health.OverallStatus);
        Assert.True(subscription.Health.FolderAccessible);
        Assert.False(subscription.Health.SqlFilesPresent);
    }

    [Fact]
    public async Task ExecuteAsync_UpdatesLastCheckedAt()
    {
        // Arrange
        var subscriptionId = Guid.NewGuid();
        var subscription = CreateSubscription(subscriptionId);
        subscription.Health.LastCheckedAt = DateTime.MinValue;
        var subscriptionRepo = new MockSubscriptionRepository(subscription);
        var connectionTester = new MockConnectionTester(success: true);
        var folderValidator = new MockFolderValidator(exists: true, sqlFileCount: 5);

        var services = CreateServices(subscriptionRepo, connectionTester, folderValidator);
        var worker = CreateWorker(services);
        var beforeCheck = DateTime.UtcNow;

        // Act
        await InvokeCheckAllSubscriptionsAsync(worker);

        // Assert
        Assert.True(subscription.Health.LastCheckedAt >= beforeCheck);
    }

    [Fact]
    public async Task ExecuteAsync_DoesNotRun_WhenDisabled()
    {
        // Arrange
        var subscriptionId = Guid.NewGuid();
        var subscription = CreateSubscription(subscriptionId);
        subscription.Health.OverallStatus = HealthStatus.Unknown;
        var subscriptionRepo = new MockSubscriptionRepository(subscription);
        var connectionTester = new MockConnectionTester(success: true);
        var folderValidator = new MockFolderValidator(exists: true, sqlFileCount: 5);

        var services = CreateServices(subscriptionRepo, connectionTester, folderValidator);
        var config = CreateConfig(enabled: false);
        var worker = new HealthCheckWorker(services, config, CreateLogger());

        // Act - Worker should exit immediately when disabled
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await worker.StartAsync(cts.Token);
        await worker.StopAsync(CancellationToken.None);

        // Assert - Health should not have been updated
        Assert.Equal(HealthStatus.Unknown, subscription.Health.OverallStatus);
    }

    private static Subscription CreateSubscription(Guid id)
    {
        return new Subscription
        {
            Id = id,
            Name = "Test",
            State = SubscriptionState.Active,
            Database = new DatabaseConnection
            {
                Server = "localhost",
                Database = "TestDb",
                AuthType = AuthenticationType.WindowsIntegrated
            },
            Project = new ProjectFolder { RootPath = "." },
            Options = new ComparisonOptions(),
            Health = new SubscriptionHealth()
        };
    }

    private static ServiceProvider CreateServices(
        MockSubscriptionRepository subscriptionRepo,
        MockConnectionTester connectionTester,
        MockFolderValidator folderValidator)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ISubscriptionRepository>(subscriptionRepo);
        services.AddSingleton<IDatabaseConnectionTester>(connectionTester);
        services.AddSingleton<IFolderValidator>(folderValidator);
        services.AddSingleton<IHubContext<SyncHub>>(new MockHubContext());
        services.AddSingleton<IRealtimeEventPublisher>(new NullRealtimeEventPublisher());
        return services.BuildServiceProvider();
    }

    private sealed class NullRealtimeEventPublisher : IRealtimeEventPublisher
    {
        public Task PublishToSubscriptionAsync<TEvent>(Guid subscriptionId, string eventName, TEvent payload, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task PublishToAllSubscriptionsAsync<TEvent>(string eventName, TEvent payload, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private static HealthCheckWorker CreateWorker(IServiceProvider services)
    {
        return new HealthCheckWorker(services, CreateConfig(), CreateLogger());
    }

    private static IOptions<ServiceConfiguration> CreateConfig(bool enabled = true)
    {
        return Options.Create(new ServiceConfiguration
        {
            Monitoring = new MonitoringSettings
            {
                HealthCheckInterval = TimeSpan.FromMinutes(1)
            },
            Workers = new WorkerSettings
            {
                EnableHealthChecks = enabled
            }
        });
    }

    private static ILogger<HealthCheckWorker> CreateLogger()
    {
        return new LoggerFactory().CreateLogger<HealthCheckWorker>();
    }

    private static async Task InvokeCheckAllSubscriptionsAsync(HealthCheckWorker worker)
    {
        var method = typeof(HealthCheckWorker).GetMethod("CheckAllSubscriptionsAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var task = (Task)method!.Invoke(worker, new object[] { CancellationToken.None })!;
        await task;
    }

    private sealed class MockSubscriptionRepository : ISubscriptionRepository
    {
        private readonly Subscription _subscription;
        public MockSubscriptionRepository(Subscription subscription) => _subscription = subscription;

        public Task<Subscription?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<Subscription?>(id == _subscription.Id ? _subscription : null);

        public Task<IReadOnlyList<Subscription>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Subscription>>(new[] { _subscription });

        public Task<Subscription?> GetByNameAsync(string name, CancellationToken cancellationToken = default) =>
            Task.FromResult<Subscription?>(_subscription.Name == name ? _subscription : null);

        public Task AddAsync(Subscription subscription, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task UpdateAsync(Subscription subscription, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }

    private sealed class MockConnectionTester : IDatabaseConnectionTester
    {
        private readonly bool _success;
        private readonly string? _error;

        public MockConnectionTester(bool success, string? error = null)
        {
            _success = success;
            _error = error;
        }

        public Task<TestConnectionResponse> TestConnectionAsync(TestConnectionRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new TestConnectionResponse
            {
                Success = _success,
                Error = _success ? null : new ErrorDetail { Message = _error ?? "Unknown error" }
            });
        }
    }

    private sealed class MockFolderValidator : IFolderValidator
    {
        private readonly bool _exists;
        private readonly int _sqlFileCount;

        public MockFolderValidator(bool exists, int sqlFileCount)
        {
            _exists = exists;
            _sqlFileCount = sqlFileCount;
        }

        public Task<ValidateFolderResponse> ValidateFolderAsync(ValidateFolderRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new ValidateFolderResponse
            {
                Exists = _exists,
                Valid = _exists && _sqlFileCount > 0,
                SqlFileCount = _sqlFileCount,
                ParseErrors = new List<FolderParseError>()
            });
        }
    }

    private sealed class MockHubContext : IHubContext<SyncHub>
    {
        public IHubClients Clients => new MockHubClients();
        public IGroupManager Groups => throw new NotImplementedException();

        private sealed class MockHubClients : IHubClients
        {
            public IClientProxy All => new MockClientProxy();
            public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => All;
            public IClientProxy Client(string connectionId) => All;
            public IClientProxy Clients(IReadOnlyList<string> connectionIds) => All;
            public IClientProxy Group(string groupName) => All;
            public IClientProxy Groups(IReadOnlyList<string> groupNames) => All;
            public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => All;
            public IClientProxy User(string userId) => All;
            public IClientProxy Users(IReadOnlyList<string> userIds) => All;
        }

        private sealed class MockClientProxy : IClientProxy
        {
            public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default) =>
                Task.CompletedTask;
        }
    }
}

