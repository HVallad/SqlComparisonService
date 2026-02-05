using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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

        var orchestrator = new ComparisonOrchestrator(subscriptions, snapshots, history, dbBuilder, new StubDatabaseSchemaReader(), fileBuilder, comparer, options, NullLogger<ComparisonOrchestrator>.Instance);

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

        var orchestrator = new ComparisonOrchestrator(subscriptions, snapshots, history, dbBuilder, new StubDatabaseSchemaReader(), fileBuilder, comparer, options, NullLogger<ComparisonOrchestrator>.Instance);

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

        var orchestrator = new ComparisonOrchestrator(subscriptions, snapshots, history, dbBuilder, new StubDatabaseSchemaReader(), fileBuilder, comparer, options, NullLogger<ComparisonOrchestrator>.Instance);

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

        public Task UpdateObjectsAsync(Guid snapshotId, IEnumerable<SchemaObjectSummary> updatedObjects, CancellationToken cancellationToken = default)
        {
            var snapshot = Snapshots.FirstOrDefault(s => s.Id == snapshotId);
            if (snapshot is null) return Task.CompletedTask;

	            foreach (var obj in updatedObjects)
	            {
	                var searchType = NormalizeFunctionType(obj.ObjectType);
	                var existingIndex = snapshot.Objects.FindIndex(o =>
	                    NormalizeFunctionType(o.ObjectType) == searchType &&
	                    string.Equals(o.SchemaName, obj.SchemaName, StringComparison.OrdinalIgnoreCase) &&
	                    string.Equals(o.ObjectName, obj.ObjectName, StringComparison.OrdinalIgnoreCase));
	                if (existingIndex >= 0)
	                {
	                    snapshot.Objects.RemoveAt(existingIndex);
	                }
	                snapshot.Objects.Add(obj);
	            }
            return Task.CompletedTask;
        }

        public Task RemoveObjectAsync(Guid snapshotId, string schemaName, string objectName, SqlObjectType objectType, CancellationToken cancellationToken = default)
        {
            var snapshot = Snapshots.FirstOrDefault(s => s.Id == snapshotId);
            if (snapshot is null) return Task.CompletedTask;

	            var searchType = NormalizeFunctionType(objectType);
	            snapshot.Objects.RemoveAll(o =>
	                NormalizeFunctionType(o.ObjectType) == searchType &&
	                string.Equals(o.SchemaName, schemaName, StringComparison.OrdinalIgnoreCase) &&
	                string.Equals(o.ObjectName, objectName, StringComparison.OrdinalIgnoreCase));
            return Task.CompletedTask;
        }

	        private static SqlObjectType NormalizeFunctionType(SqlObjectType type)
	        {
	            return type == SqlObjectType.ScalarFunction ||
	                   type == SqlObjectType.TableValuedFunction ||
	                   type == SqlObjectType.InlineTableValuedFunction
	                ? SqlObjectType.ScalarFunction
	                : type;
	        }

        public Task UpdateAsync(SchemaSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            // Snapshot is already in the list by reference, no update needed
            return Task.CompletedTask;
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
        public int FilteredCallCount { get; private set; }
        public SqlObjectType? LastFilterObjectType { get; private set; }
        public SchemaSnapshot SnapshotToReturn { get; set; } = new();

        public Task<SchemaSnapshot> BuildSnapshotAsync(Guid subscriptionId, DatabaseConnection connection, CancellationToken cancellationToken = default)
        {
            CallCount++;
            SnapshotToReturn.SubscriptionId = subscriptionId;
            return Task.FromResult(SnapshotToReturn);
        }

        public Task<SchemaSnapshot> BuildSnapshotAsync(Guid subscriptionId, DatabaseConnection connection, SqlObjectType? filterObjectType, CancellationToken cancellationToken = default)
        {
            FilteredCallCount++;
            LastFilterObjectType = filterObjectType;
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

    private sealed class StubDatabaseSchemaReader : IDatabaseSchemaReader
    {
        public IReadOnlyList<SchemaObjectSummary> ObjectsToReturn { get; set; } = Array.Empty<SchemaObjectSummary>();

        public Task<IReadOnlyList<SchemaObjectSummary>> GetAllObjectsAsync(DatabaseConnection connection, CancellationToken cancellationToken = default)
            => Task.FromResult(ObjectsToReturn);

        public Task<SchemaObjectSummary?> GetObjectAsync(DatabaseConnection connection, string schemaName, string objectName, SqlObjectType objectType, CancellationToken cancellationToken = default)
            => Task.FromResult(ObjectsToReturn.FirstOrDefault(o =>
                string.Equals(o.SchemaName, schemaName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(o.ObjectName, objectName, StringComparison.OrdinalIgnoreCase) &&
                o.ObjectType == objectType));

        public Task<IReadOnlyList<SchemaObjectSummary>> GetObjectsByTypeAsync(DatabaseConnection connection, SqlObjectType objectType, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<SchemaObjectSummary>>(ObjectsToReturn.Where(o => o.ObjectType == objectType).ToList());

        public Task<IReadOnlyList<SchemaObjectSummary>> GetObjectsAsync(DatabaseConnection connection, IEnumerable<SqlSyncService.Domain.Changes.ObjectIdentifier> objectsToQuery, CancellationToken cancellationToken = default)
        {
            var identifiers = objectsToQuery.ToList();
            var results = ObjectsToReturn.Where(o =>
                identifiers.Any(id =>
                    string.Equals(id.SchemaName, o.SchemaName, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(id.ObjectName, o.ObjectName, StringComparison.OrdinalIgnoreCase) &&
                    id.ObjectType == o.ObjectType)).ToList();
            return Task.FromResult<IReadOnlyList<SchemaObjectSummary>>(results);
        }
    }

    #region CompareObjectAsync Tests

    [Fact]
    public async Task CompareObjectAsync_Returns_Synchronized_When_Hashes_Match()
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
                Objects = new List<SchemaObjectSummary>
                {
                    new()
                    {
                        SchemaName = "dbo",
                        ObjectName = "TestProc",
                        ObjectType = SqlObjectType.StoredProcedure,
                        DefinitionHash = "ABC123",
                        DefinitionScript = "CREATE PROCEDURE [dbo].[TestProc] AS SELECT 1"
                    }
                }
            }
        };

        var fileBuilder = new StubFileModelBuilder
        {
            CacheToReturn = new FileModelCache
            {
                SubscriptionId = subscriptionId,
                FileEntries = new Dictionary<string, FileObjectEntry>
                {
                    ["TestProc.sql"] = new()
                    {
                        FilePath = "dbo/StoredProcedures/TestProc.sql",
                        ObjectName = "TestProc",
                        ObjectType = SqlObjectType.StoredProcedure,
                        ContentHash = "ABC123",
                        Content = "CREATE PROCEDURE [dbo].[TestProc] AS SELECT 1"
                    }
                }
            }
        };

        var orchestrator = new ComparisonOrchestrator(
            subscriptions, snapshots, history, dbBuilder, new StubDatabaseSchemaReader(), fileBuilder, new StubSchemaComparer(), CreateOptions(1), NullLogger<ComparisonOrchestrator>.Instance);

        // Act
        var result = await orchestrator.CompareObjectAsync(
            subscriptionId, "dbo", "TestProc", SqlObjectType.StoredProcedure);

        // Assert
        Assert.True(result.IsSynchronized);
        Assert.True(result.ExistsInDatabase);
        Assert.True(result.ExistsInFileSystem);
        Assert.Null(result.Difference);
    }

    [Fact]
    public async Task CompareObjectAsync_Returns_Modify_Difference_When_Hashes_Differ()
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
                Objects = new List<SchemaObjectSummary>
                {
                    new()
                    {
                        SchemaName = "dbo",
                        ObjectName = "TestProc",
                        ObjectType = SqlObjectType.StoredProcedure,
                        DefinitionHash = "DB_HASH",
                        DefinitionScript = "CREATE PROCEDURE [dbo].[TestProc] AS SELECT 1"
                    }
                }
            }
        };

        var fileBuilder = new StubFileModelBuilder
        {
            CacheToReturn = new FileModelCache
            {
                SubscriptionId = subscriptionId,
                FileEntries = new Dictionary<string, FileObjectEntry>
                {
                    ["TestProc.sql"] = new()
                    {
                        FilePath = "dbo/StoredProcedures/TestProc.sql",
                        ObjectName = "TestProc",
                        ObjectType = SqlObjectType.StoredProcedure,
                        ContentHash = "FILE_HASH",
                        Content = "CREATE PROCEDURE [dbo].[TestProc] AS SELECT 2"
                    }
                }
            }
        };

        var orchestrator = new ComparisonOrchestrator(
            subscriptions, snapshots, history, dbBuilder, new StubDatabaseSchemaReader(), fileBuilder, new StubSchemaComparer(), CreateOptions(1), NullLogger<ComparisonOrchestrator>.Instance);

        // Act
        var result = await orchestrator.CompareObjectAsync(
            subscriptionId, "dbo", "TestProc", SqlObjectType.StoredProcedure);

        // Assert
        Assert.False(result.IsSynchronized);
        Assert.True(result.ExistsInDatabase);
        Assert.True(result.ExistsInFileSystem);
        Assert.NotNull(result.Difference);
        Assert.Equal(DifferenceType.Modify, result.Difference!.DifferenceType);
    }

    [Fact]
    public async Task CompareObjectAsync_Returns_Delete_When_Only_In_Database()
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
        var dbBuilder = new StubDatabaseModelBuilder
        {
            SnapshotToReturn = new SchemaSnapshot
            {
                SubscriptionId = subscriptionId,
                Objects = new List<SchemaObjectSummary>
                {
                    new()
                    {
                        SchemaName = "dbo",
                        ObjectName = "TestProc",
                        ObjectType = SqlObjectType.StoredProcedure,
                        DefinitionHash = "ABC123",
                        DefinitionScript = "CREATE PROCEDURE ..."
                    }
                }
            }
        };

        var fileBuilder = new StubFileModelBuilder
        {
            CacheToReturn = new FileModelCache { SubscriptionId = subscriptionId }
        };

        var orchestrator = new ComparisonOrchestrator(
            subscriptions, new InMemorySchemaSnapshotRepository(), new InMemoryComparisonHistoryRepository(),
            dbBuilder, new StubDatabaseSchemaReader(), fileBuilder, new StubSchemaComparer(), CreateOptions(1), NullLogger<ComparisonOrchestrator>.Instance);

        // Act
        var result = await orchestrator.CompareObjectAsync(
            subscriptionId, "dbo", "TestProc", SqlObjectType.StoredProcedure);

        // Assert
        Assert.False(result.IsSynchronized);
        Assert.True(result.ExistsInDatabase);
        Assert.False(result.ExistsInFileSystem);
        Assert.NotNull(result.Difference);
        Assert.Equal(DifferenceType.Delete, result.Difference!.DifferenceType);
    }

    [Fact]
    public async Task CompareObjectAsync_Returns_Add_When_Only_In_Files()
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
        var dbBuilder = new StubDatabaseModelBuilder
        {
            SnapshotToReturn = new SchemaSnapshot { SubscriptionId = subscriptionId }
        };

        var fileBuilder = new StubFileModelBuilder
        {
            CacheToReturn = new FileModelCache
            {
                SubscriptionId = subscriptionId,
                FileEntries = new Dictionary<string, FileObjectEntry>
                {
                    ["NewProc.sql"] = new()
                    {
                        FilePath = "dbo/StoredProcedures/NewProc.sql",
                        ObjectName = "NewProc",
                        ObjectType = SqlObjectType.StoredProcedure,
                        ContentHash = "XYZ789",
                        Content = "CREATE PROCEDURE ..."
                    }
                }
            }
        };

        var orchestrator = new ComparisonOrchestrator(
            subscriptions, new InMemorySchemaSnapshotRepository(), new InMemoryComparisonHistoryRepository(),
            dbBuilder, new StubDatabaseSchemaReader(), fileBuilder, new StubSchemaComparer(), CreateOptions(1), NullLogger<ComparisonOrchestrator>.Instance);

        // Act
        var result = await orchestrator.CompareObjectAsync(
            subscriptionId, "dbo", "NewProc", SqlObjectType.StoredProcedure);

        // Assert
        Assert.False(result.IsSynchronized);
        Assert.False(result.ExistsInDatabase);
        Assert.True(result.ExistsInFileSystem);
        Assert.NotNull(result.Difference);
        Assert.Equal(DifferenceType.Add, result.Difference!.DifferenceType);
    }

    [Fact]
    public async Task CompareObjectAsync_Returns_Synchronized_When_Object_Not_Found_Anywhere()
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
        var dbBuilder = new StubDatabaseModelBuilder
        {
            SnapshotToReturn = new SchemaSnapshot { SubscriptionId = subscriptionId }
        };
        var fileBuilder = new StubFileModelBuilder
        {
            CacheToReturn = new FileModelCache { SubscriptionId = subscriptionId }
        };

        var orchestrator = new ComparisonOrchestrator(
            subscriptions, new InMemorySchemaSnapshotRepository(), new InMemoryComparisonHistoryRepository(),
            dbBuilder, new StubDatabaseSchemaReader(), fileBuilder, new StubSchemaComparer(), CreateOptions(1), NullLogger<ComparisonOrchestrator>.Instance);

        // Act
        var result = await orchestrator.CompareObjectAsync(
            subscriptionId, "dbo", "NonExistent", SqlObjectType.StoredProcedure);

        // Assert
        Assert.True(result.IsSynchronized);
        Assert.False(result.ExistsInDatabase);
        Assert.False(result.ExistsInFileSystem);
        Assert.Null(result.Difference);
    }

    [Fact]
    public async Task CompareObjectAsync_Throws_SubscriptionNotFound_For_Invalid_Id()
    {
        // Arrange
        var subscriptions = new InMemorySubscriptionRepository { Subscription = null };
        var orchestrator = new ComparisonOrchestrator(
            subscriptions, new InMemorySchemaSnapshotRepository(), new InMemoryComparisonHistoryRepository(),
            new StubDatabaseModelBuilder(), new StubDatabaseSchemaReader(), new StubFileModelBuilder(), new StubSchemaComparer(), CreateOptions(1), NullLogger<ComparisonOrchestrator>.Instance);

        // Act & Assert
        await Assert.ThrowsAsync<SubscriptionNotFoundException>(() =>
            orchestrator.CompareObjectAsync(Guid.NewGuid(), "dbo", "Test", SqlObjectType.Table));
    }

    #endregion
}

