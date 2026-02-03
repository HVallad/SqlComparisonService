using Microsoft.Extensions.Options;
using SqlSyncService.Configuration;
using SqlSyncService.DacFx;
using SqlSyncService.Domain.Caching;
using SqlSyncService.Domain.Comparisons;
using SqlSyncService.Domain.Subscriptions;
using SqlSyncService.Persistence;
using SqlSyncService.Services;
using System.Linq;

namespace SqlSyncService.Tests.Services;

public class ComparisonOrchestratorTests
{
    [Fact]
    public async Task RunComparisonAsync_FullComparison_Persists_Snapshot_And_Result()
    {
        // Arrange
        var subscriptionId = Guid.NewGuid();
        var subscription = new Subscription
        {
            Id = subscriptionId,
            Name = "Test",
            Database = new DatabaseConnection { Database = "Db" },
            Project = new ProjectFolder { RootPath = "." },
            Options = new ComparisonOptions()
        };

        var subscriptions = new InMemorySubscriptionRepository { Subscription = subscription };
        var snapshots = new InMemorySchemaSnapshotRepository();
        var history = new InMemoryComparisonHistoryRepository();

        var dbBuilder = new StubDatabaseModelBuilder
        {
            SnapshotToReturn = new SchemaSnapshot
            {
                SubscriptionId = subscriptionId,
                CapturedAt = DateTime.UtcNow
            }
        };

        var fileBuilder = new StubFileModelBuilder
        {
            CacheToReturn = new FileModelCache
            {
                SubscriptionId = subscriptionId,
                CapturedAt = DateTime.UtcNow
            }
        };

        var differences = new List<SchemaDifference>
        {
            new()
            {
                Id = Guid.NewGuid(),
                ObjectName = "Users",
                ObjectType = SqlObjectType.Table,
                DifferenceType = DifferenceType.Add
            }
        };

        var comparer = new StubSchemaComparer { DifferencesToReturn = differences };
        var options = CreateOptions(1);

        var orchestrator = new ComparisonOrchestrator(subscriptions, snapshots, history, dbBuilder, fileBuilder, comparer, options);

        // Act
        var result = await orchestrator.RunComparisonAsync(subscriptionId, fullComparison: true);

        // Assert
        Assert.NotEqual(Guid.Empty, result.Id);
        Assert.Equal(subscriptionId, result.SubscriptionId);
        Assert.Equal(ComparisonStatus.HasDifferences, result.Status);
        Assert.Single(history.Results);
        Assert.Single(snapshots.Snapshots);
        Assert.NotNull(subscriptions.Subscription!.LastComparedAt);
        Assert.Equal(1, dbBuilder.CallCount);
        Assert.Equal(1, result.Summary.TotalDifferences);
        Assert.Equal(1, result.Summary.Additions);
        Assert.Equal(0, result.Summary.ObjectsCompared);
        Assert.Equal(0, result.Summary.ObjectsUnchanged);
    }

    [Fact]
    public async Task RunComparisonAsync_Populates_Unsupported_Object_Counts_And_List()
    {
        // Arrange
        var subscriptionId = Guid.NewGuid();
        var subscription = new Subscription
        {
            Id = subscriptionId,
            Name = "Test",
            Database = new DatabaseConnection { Database = "Db" },
            Project = new ProjectFolder { RootPath = "." },
            Options = new ComparisonOptions()
        };

        var subscriptions = new InMemorySubscriptionRepository { Subscription = subscription };
        var snapshots = new InMemorySchemaSnapshotRepository();
        var history = new InMemoryComparisonHistoryRepository();

        var dbBuilder = new StubDatabaseModelBuilder
        {
            SnapshotToReturn = new SchemaSnapshot
            {
                SubscriptionId = subscriptionId,
                CapturedAt = DateTime.UtcNow,
                Objects =
                    {
                        new SchemaObjectSummary
                        {
                            SchemaName = "dbo",
                            ObjectName = "Users",
                            ObjectType = SqlObjectType.Table,
                            DefinitionHash = "hash-users-db"
                        },
                        new SchemaObjectSummary
                        {
                            SchemaName = "master",
                            ObjectName = "AppLogin",
                            ObjectType = SqlObjectType.Login,
                            DefinitionHash = string.Empty
                        }
                    }
            }
        };

        var fileBuilder = new StubFileModelBuilder
        {
            CacheToReturn = new FileModelCache
            {
                SubscriptionId = subscriptionId,
                CapturedAt = DateTime.UtcNow,
                FileEntries =
                    {
                        ["dbo.Users.sql"] = new FileObjectEntry
                        {
                            FilePath = "dbo/Tables/Users.sql",
                            ObjectName = "Users",
                            ObjectType = SqlObjectType.Table,
                            ContentHash = "hash-users-file",
                            LastModified = DateTime.UtcNow
                        },
                        ["Misc/Unknown.sql"] = new FileObjectEntry
                        {
                            FilePath = "Misc/Unknown.sql",
                            ObjectName = "SomeArtifact",
                            ObjectType = SqlObjectType.Unknown,
                            ContentHash = "hash-unknown-file",
                            LastModified = DateTime.UtcNow
                        }
                    }
            }
        };

        var comparer = new StubSchemaComparer { DifferencesToReturn = Array.Empty<SchemaDifference>() };
        var options = CreateOptions(1);

        var orchestrator = new ComparisonOrchestrator(subscriptions, snapshots, history, dbBuilder, fileBuilder, comparer, options);

        // Act
        var result = await orchestrator.RunComparisonAsync(subscriptionId, fullComparison: true);

        // Assert
        Assert.Equal(ComparisonStatus.Synchronized, result.Status);
        Assert.Equal(1, result.Summary.UnsupportedDatabaseObjectCount);
        Assert.Equal(1, result.Summary.UnsupportedFileObjectCount);
        Assert.Equal(1, result.Summary.ObjectsCompared);
        Assert.Equal(1, result.Summary.ObjectsUnchanged);

        Assert.Equal(2, result.UnsupportedObjects.Count);
        var login = Assert.Single(result.UnsupportedObjects.Where(o => o.ObjectType == SqlObjectType.Login));
        Assert.Equal(DifferenceSource.Database, login.Source);
        Assert.Equal("AppLogin", login.ObjectName);

        var unknown = Assert.Single(result.UnsupportedObjects.Where(o => o.ObjectType == SqlObjectType.Unknown));
        Assert.Equal(DifferenceSource.FileSystem, unknown.Source);
        Assert.Equal("SomeArtifact", unknown.ObjectName);
    }

    [Fact]
    public async Task RunComparisonAsync_Incremental_Uses_Existing_Snapshot()
    {
        // Arrange
        var subscriptionId = Guid.NewGuid();
        var subscription = new Subscription
        {
            Id = subscriptionId,
            Name = "Test",
            Database = new DatabaseConnection { Database = "Db" },
            Project = new ProjectFolder { RootPath = "." },
            Options = new ComparisonOptions()
        };

        var subscriptions = new InMemorySubscriptionRepository { Subscription = subscription };
        var snapshots = new InMemorySchemaSnapshotRepository();
        var existingSnapshot = new SchemaSnapshot
        {
            SubscriptionId = subscriptionId,
            CapturedAt = DateTime.UtcNow.AddMinutes(-5)
        };
        await snapshots.AddAsync(existingSnapshot);

        var history = new InMemoryComparisonHistoryRepository();
        var dbBuilder = new StubDatabaseModelBuilder();
        var fileBuilder = new StubFileModelBuilder
        {
            CacheToReturn = new FileModelCache
            {
                SubscriptionId = subscriptionId,
                CapturedAt = DateTime.UtcNow
            }
        };

        var comparer = new StubSchemaComparer { DifferencesToReturn = Array.Empty<SchemaDifference>() };
        var options = CreateOptions(1);

        var orchestrator = new ComparisonOrchestrator(subscriptions, snapshots, history, dbBuilder, fileBuilder, comparer, options);

        // Act
        var result = await orchestrator.RunComparisonAsync(subscriptionId, fullComparison: false);

        // Assert
        Assert.Equal(ComparisonStatus.Synchronized, result.Status);
        Assert.Single(history.Results);
        Assert.Single(snapshots.Snapshots); // No new snapshot should be created
        Assert.Equal(0, dbBuilder.CallCount);
    }

    private static IOptions<ServiceConfiguration> CreateOptions(int maxConcurrent)
    {
        var config = new ServiceConfiguration();
        config.Monitoring.MaxConcurrentComparisons = maxConcurrent;
        return Options.Create(config);
    }

    private sealed class InMemorySubscriptionRepository : ISubscriptionRepository
    {
        public Subscription? Subscription { get; set; }

        public Task<Subscription?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(Subscription);

        public Task<IReadOnlyList<Subscription>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Subscription>>(Subscription is null ? Array.Empty<Subscription>() : new[] { Subscription });

        public Task AddAsync(Subscription subscription, CancellationToken cancellationToken = default)
        {
            Subscription = subscription;
            return Task.CompletedTask;
        }

        public Task UpdateAsync(Subscription subscription, CancellationToken cancellationToken = default)
        {
            Subscription = subscription;
            return Task.CompletedTask;
        }

        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
        {
            Subscription = null;
            return Task.FromResult(true);
        }
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
            Task.FromResult<IReadOnlyList<SchemaSnapshot>>(Snapshots.Where(s => s.SubscriptionId == subscriptionId).OrderBy(s => s.CapturedAt).ToList());

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
            var forSubscription = Snapshots
                .Where(s => s.SubscriptionId == subscriptionId)
                .OrderByDescending(s => s.CapturedAt)
                .ToList();

            if (forSubscription.Count <= maxCount)
            {
                return Task.FromResult(0);
            }

            var toRemove = forSubscription.Skip(maxCount).ToList();
            foreach (var item in toRemove)
            {
                Snapshots.Remove(item);
            }
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

        public Task<IReadOnlyList<ComparisonResult>> GetBySubscriptionAsync(Guid subscriptionId, int? maxCount = null, CancellationToken cancellationToken = default)
        {
            IEnumerable<ComparisonResult> query = Results
                .Where(r => r.SubscriptionId == subscriptionId)
                .OrderByDescending(r => r.ComparedAt);

            if (maxCount.HasValue)
            {
                query = query.Take(maxCount.Value);
            }

            return Task.FromResult<IReadOnlyList<ComparisonResult>>(query.ToList());
        }

        public Task<int> DeleteBySubscriptionAsync(Guid subscriptionId, CancellationToken cancellationToken = default)
        {
            var before = Results.Count;
            Results.RemoveAll(r => r.SubscriptionId == subscriptionId);
            var removed = before - Results.Count;
            return Task.FromResult(removed);
        }

        public Task<int> DeleteOlderThanAsync(DateTime cutoffDate, CancellationToken cancellationToken = default)
        {
            var before = Results.Count;
            Results.RemoveAll(r => r.ComparedAt < cutoffDate);
            return Task.FromResult(before - Results.Count);
        }
    }

    private sealed class StubDatabaseModelBuilder : IDatabaseModelBuilder
    {
        public int CallCount { get; private set; }
        public SchemaSnapshot SnapshotToReturn { get; set; } = new();

        public Task<SchemaSnapshot> BuildSnapshotAsync(Guid subscriptionId, DatabaseConnection connection, CancellationToken cancellationToken = default)
        {
            CallCount++;
            SnapshotToReturn.SubscriptionId = subscriptionId;
            return Task.FromResult(SnapshotToReturn);
        }
    }

    private sealed class StubFileModelBuilder : IFileModelBuilder
    {
        public FileModelCache CacheToReturn { get; set; } = new();

        public Task<FileModelCache> BuildCacheAsync(Guid subscriptionId, ProjectFolder folder, CancellationToken cancellationToken = default)
        {
            CacheToReturn.SubscriptionId = subscriptionId;
            return Task.FromResult(CacheToReturn);
        }
    }

    private sealed class StubSchemaComparer : ISchemaComparer
    {
        public IReadOnlyList<SchemaDifference> DifferencesToReturn { get; set; } = Array.Empty<SchemaDifference>();

        public Task<IReadOnlyList<SchemaDifference>> CompareAsync(SchemaSnapshot dbSnapshot, FileModelCache fileCache, ComparisonOptions options, CancellationToken cancellationToken = default) =>
            Task.FromResult(DifferencesToReturn);
    }
}

