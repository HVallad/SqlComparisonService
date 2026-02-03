using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using SqlSyncService.ChangeDetection;
using SqlSyncService.Domain.Changes;
using SqlSyncService.Domain.Comparisons;
using SqlSyncService.Domain.Subscriptions;
using SqlSyncService.Persistence;
using SqlSyncService.Realtime;
using SqlSyncService.Services;

namespace SqlSyncService.Tests.ChangeDetection;

public class ChangeProcessorTests
{
    private readonly MockSubscriptionRepository _subscriptionRepo = new();
    private readonly MockPendingChangeRepository _pendingChangeRepo = new();
    private readonly MockComparisonOrchestrator _orchestrator = new();
    private readonly MockHubContext _hubContext = new();
    private readonly ChangeProcessor _processor;

    public ChangeProcessorTests()
    {
        var logger = new LoggerFactory().CreateLogger<ChangeProcessor>();
        _processor = new ChangeProcessor(
            _subscriptionRepo,
            _pendingChangeRepo,
            _orchestrator,
            _hubContext,
            logger);
    }

    [Fact]
    public async Task ProcessBatchAsync_PersistsAllChanges()
    {
        // Arrange
        var subscriptionId = Guid.NewGuid();
        _subscriptionRepo.Subscription = CreateSubscription(subscriptionId, SubscriptionState.Active);
        var batch = CreateBatch(subscriptionId, 3);

        // Act
        await _processor.ProcessBatchAsync(batch);

        // Assert
        Assert.Equal(3, _pendingChangeRepo.AddedChanges.Count);
    }

    [Fact]
    public async Task ProcessBatchAsync_SendsSignalRNotification()
    {
        // Arrange
        var subscriptionId = Guid.NewGuid();
        _subscriptionRepo.Subscription = CreateSubscription(subscriptionId, SubscriptionState.Active);
        var batch = CreateBatch(subscriptionId, 2);

        // Act
        await _processor.ProcessBatchAsync(batch);

        // Assert
        Assert.True(_hubContext.ChangesDetectedSent);
    }

    [Fact]
    public async Task ProcessBatchAsync_TriggersComparison_WhenAutoCompareAndCompareOnFileChangeEnabled()
    {
        // Arrange
        var subscriptionId = Guid.NewGuid();
        _subscriptionRepo.Subscription = CreateSubscription(subscriptionId, SubscriptionState.Active, 
            autoCompare: true, compareOnFileChange: true);
        var batch = CreateBatch(subscriptionId, 1, ChangeSource.FileSystem);

        // Act
        await _processor.ProcessBatchAsync(batch);

        // Assert
        Assert.True(_orchestrator.ComparisonTriggered);
        Assert.Equal(subscriptionId, _orchestrator.LastSubscriptionId);
    }

    [Fact]
    public async Task ProcessBatchAsync_DoesNotTriggerComparison_WhenAutoCompareDisabled()
    {
        // Arrange
        var subscriptionId = Guid.NewGuid();
        _subscriptionRepo.Subscription = CreateSubscription(subscriptionId, SubscriptionState.Active,
            autoCompare: false, compareOnFileChange: true);
        var batch = CreateBatch(subscriptionId, 1, ChangeSource.FileSystem);

        // Act
        await _processor.ProcessBatchAsync(batch);

        // Assert
        Assert.False(_orchestrator.ComparisonTriggered);
    }

    [Fact]
    public async Task ProcessBatchAsync_DoesNotTriggerComparison_WhenCompareOnFileChangeDisabled()
    {
        // Arrange
        var subscriptionId = Guid.NewGuid();
        _subscriptionRepo.Subscription = CreateSubscription(subscriptionId, SubscriptionState.Active,
            autoCompare: true, compareOnFileChange: false);
        var batch = CreateBatch(subscriptionId, 1, ChangeSource.FileSystem);

        // Act
        await _processor.ProcessBatchAsync(batch);

        // Assert
        Assert.False(_orchestrator.ComparisonTriggered);
    }

    [Fact]
    public async Task ProcessBatchAsync_TriggersComparison_ForDatabaseChanges()
    {
        // Arrange
        var subscriptionId = Guid.NewGuid();
        _subscriptionRepo.Subscription = CreateSubscription(subscriptionId, SubscriptionState.Active,
            autoCompare: true, compareOnDatabaseChange: true);
        var batch = CreateBatch(subscriptionId, 1, ChangeSource.Database);

        // Act
        await _processor.ProcessBatchAsync(batch);

        // Assert
        Assert.True(_orchestrator.ComparisonTriggered);
    }

    [Fact]
    public async Task ProcessBatchAsync_DoesNotTriggerComparison_WhenSubscriptionNotActive()
    {
        // Arrange
        var subscriptionId = Guid.NewGuid();
        _subscriptionRepo.Subscription = CreateSubscription(subscriptionId, SubscriptionState.Paused,
            autoCompare: true, compareOnFileChange: true);
        var batch = CreateBatch(subscriptionId, 1, ChangeSource.FileSystem);

        // Act
        await _processor.ProcessBatchAsync(batch);

        // Assert
        Assert.False(_orchestrator.ComparisonTriggered);
    }

    [Fact]
    public async Task ProcessBatchAsync_DoesNothing_WhenBatchIsEmpty()
    {
        // Arrange
        var batch = new PendingChangeBatch { SubscriptionId = Guid.NewGuid(), Changes = new List<DetectedChange>() };

        // Act
        await _processor.ProcessBatchAsync(batch);

        // Assert
        Assert.Empty(_pendingChangeRepo.AddedChanges);
        Assert.False(_hubContext.ChangesDetectedSent);
    }

    [Fact]
    public async Task ProcessBatchAsync_MarksChangesAsProcessed_AfterComparison()
    {
        // Arrange
        var subscriptionId = Guid.NewGuid();
        _subscriptionRepo.Subscription = CreateSubscription(subscriptionId, SubscriptionState.Active,
            autoCompare: true, compareOnFileChange: true);
        var batch = CreateBatch(subscriptionId, 2, ChangeSource.FileSystem);

        // Act
        await _processor.ProcessBatchAsync(batch);

        // Assert
        Assert.Equal(2, _pendingChangeRepo.MarkedAsProcessedCount);
    }

    [Fact]
    public async Task ProcessBatchAsync_LeavesChangesUnprocessed_WhenComparisonInProgress()
    {
        // Arrange
        var subscriptionId = Guid.NewGuid();
        _subscriptionRepo.Subscription = CreateSubscription(subscriptionId, SubscriptionState.Active,
            autoCompare: true, compareOnFileChange: true);
        _orchestrator.ThrowComparisonInProgress = true;
        var batch = CreateBatch(subscriptionId, 2, ChangeSource.FileSystem);

        // Act
        await _processor.ProcessBatchAsync(batch);

        // Assert
        Assert.Equal(0, _pendingChangeRepo.MarkedAsProcessedCount);
    }

    private static Subscription CreateSubscription(
        Guid id,
        SubscriptionState state,
        bool autoCompare = false,
        bool compareOnFileChange = false,
        bool compareOnDatabaseChange = false)
    {
        return new Subscription
        {
            Id = id,
            Name = "Test",
            State = state,
            Database = new DatabaseConnection { Database = "Test" },
            Project = new ProjectFolder { RootPath = "." },
            Options = new ComparisonOptions
            {
                AutoCompare = autoCompare,
                CompareOnFileChange = compareOnFileChange,
                CompareOnDatabaseChange = compareOnDatabaseChange
            }
        };
    }

    private static PendingChangeBatch CreateBatch(
        Guid subscriptionId,
        int changeCount,
        ChangeSource source = ChangeSource.FileSystem)
    {
        var changes = Enumerable.Range(1, changeCount)
            .Select(i => new DetectedChange
            {
                Id = Guid.NewGuid(),
                SubscriptionId = subscriptionId,
                ObjectIdentifier = $"dbo.Table{i}",
                Source = source,
                Type = ChangeType.Modified,
                DetectedAt = DateTime.UtcNow
            })
            .ToList();

        return new PendingChangeBatch
        {
            SubscriptionId = subscriptionId,
            Changes = changes,
            BatchStartedAt = DateTime.UtcNow.AddSeconds(-1),
            BatchCompletedAt = DateTime.UtcNow
        };
    }

    private sealed class MockSubscriptionRepository : ISubscriptionRepository
    {
        public Subscription? Subscription { get; set; }

        public Task<Subscription?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(Subscription);

        public Task<IReadOnlyList<Subscription>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Subscription>>(Subscription != null ? new[] { Subscription } : Array.Empty<Subscription>());

        public Task<Subscription?> GetByNameAsync(string name, CancellationToken cancellationToken = default) =>
            Task.FromResult(Subscription?.Name == name ? Subscription : null);

        public Task AddAsync(Subscription subscription, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task UpdateAsync(Subscription subscription, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }

    private sealed class MockPendingChangeRepository : IPendingChangeRepository
    {
        public List<DetectedChange> AddedChanges { get; } = new();
        public int MarkedAsProcessedCount { get; private set; }

        public Task AddAsync(DetectedChange change, CancellationToken cancellationToken = default)
        {
            AddedChanges.Add(change);
            return Task.CompletedTask;
        }

        public Task MarkAsProcessedAsync(Guid id, CancellationToken cancellationToken = default)
        {
            MarkedAsProcessedCount++;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<DetectedChange>> GetPendingForSubscriptionAsync(Guid subscriptionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DetectedChange>>(AddedChanges.Where(c => !c.IsProcessed).ToList());

        public Task<int> DeleteProcessedOlderThanAsync(DateTime cutoffDate, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
        {
            AddedChanges.RemoveAll(c => c.Id == id);
            return Task.FromResult(true);
        }
    }

    private sealed class MockComparisonOrchestrator : IComparisonOrchestrator
    {
        public bool ComparisonTriggered { get; private set; }
        public Guid? LastSubscriptionId { get; private set; }
        public bool ThrowComparisonInProgress { get; set; }

        public Task<ComparisonResult> RunComparisonAsync(Guid subscriptionId, bool fullComparison, CancellationToken cancellationToken = default)
        {
            if (ThrowComparisonInProgress)
            {
                throw new ComparisonInProgressException();
            }

            ComparisonTriggered = true;
            LastSubscriptionId = subscriptionId;
            return Task.FromResult(new ComparisonResult
            {
                Id = Guid.NewGuid(),
                SubscriptionId = subscriptionId,
                Status = ComparisonStatus.Synchronized
            });
        }
    }

    private sealed class MockHubContext : IHubContext<SyncHub>
    {
        public bool ChangesDetectedSent { get; private set; }

        public IHubClients Clients => new MockHubClients(this);

        public IGroupManager Groups => throw new NotImplementedException();

        private sealed class MockHubClients : IHubClients
        {
            private readonly MockHubContext _parent;
            public MockHubClients(MockHubContext parent) => _parent = parent;

            public IClientProxy All => new MockClientProxy(_parent);
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
            private readonly MockHubContext _parent;
            public MockClientProxy(MockHubContext parent) => _parent = parent;

            public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
            {
                if (method == "ChangesDetected")
                {
                    _parent.ChangesDetectedSent = true;
                }
                return Task.CompletedTask;
            }
        }
    }
}

