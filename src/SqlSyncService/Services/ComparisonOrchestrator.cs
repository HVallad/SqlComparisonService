using System.Diagnostics;
using System.Linq;
using Microsoft.Extensions.Options;
using SqlSyncService.Configuration;
using SqlSyncService.DacFx;
using SqlSyncService.Domain.Caching;
using SqlSyncService.Domain.Comparisons;
using SqlSyncService.Domain.Subscriptions;
using SqlSyncService.Persistence;

namespace SqlSyncService.Services;

public interface IComparisonOrchestrator
{
    Task<ComparisonResult> RunComparisonAsync(Guid subscriptionId, bool fullComparison, CancellationToken cancellationToken = default);
}

public sealed class ComparisonOrchestrator : IComparisonOrchestrator
{
    private static SemaphoreSlim? _semaphore;
    private static readonly object _lock = new();

    private readonly ISubscriptionRepository _subscriptionRepository;
    private readonly ISchemaSnapshotRepository _schemaSnapshotRepository;
    private readonly IComparisonHistoryRepository _comparisonHistoryRepository;
    private readonly IDatabaseModelBuilder _databaseModelBuilder;
    private readonly IFileModelBuilder _fileModelBuilder;
    private readonly ISchemaComparer _schemaComparer;

    public ComparisonOrchestrator(
        ISubscriptionRepository subscriptionRepository,
        ISchemaSnapshotRepository schemaSnapshotRepository,
        IComparisonHistoryRepository comparisonHistoryRepository,
        IDatabaseModelBuilder databaseModelBuilder,
        IFileModelBuilder fileModelBuilder,
        ISchemaComparer schemaComparer,
        IOptions<ServiceConfiguration> options)
    {
        _subscriptionRepository = subscriptionRepository ?? throw new ArgumentNullException(nameof(subscriptionRepository));
        _schemaSnapshotRepository = schemaSnapshotRepository ?? throw new ArgumentNullException(nameof(schemaSnapshotRepository));
        _comparisonHistoryRepository = comparisonHistoryRepository ?? throw new ArgumentNullException(nameof(comparisonHistoryRepository));
        _databaseModelBuilder = databaseModelBuilder ?? throw new ArgumentNullException(nameof(databaseModelBuilder));
        _fileModelBuilder = fileModelBuilder ?? throw new ArgumentNullException(nameof(fileModelBuilder));
        _schemaComparer = schemaComparer ?? throw new ArgumentNullException(nameof(schemaComparer));

        if (_semaphore == null)
        {
            lock (_lock)
            {
                if (_semaphore == null)
                {
                    var max = options?.Value.Monitoring.MaxConcurrentComparisons ?? 1;
                    _semaphore = new SemaphoreSlim(max, max);
                }
            }
        }
    }

    public async Task<ComparisonResult> RunComparisonAsync(Guid subscriptionId, bool fullComparison, CancellationToken cancellationToken = default)
    {
        if (subscriptionId == Guid.Empty) throw new ArgumentException("SubscriptionId must not be empty.", nameof(subscriptionId));

        var semaphore = _semaphore ?? throw new InvalidOperationException("Comparison semaphore is not initialized.");
        var acquired = false;

        try
        {
            acquired = await semaphore.WaitAsync(0, cancellationToken).ConfigureAwait(false);
            if (!acquired)
            {
                throw new ComparisonInProgressException("A comparison is already in progress. Please try again later.");
            }

            var subscription = await _subscriptionRepository.GetByIdAsync(subscriptionId, cancellationToken).ConfigureAwait(false)
                    ?? throw new SubscriptionNotFoundException(subscriptionId);

            var stopwatch = Stopwatch.StartNew();

            SchemaSnapshot snapshot;
            var builtNewSnapshot = false;

            if (fullComparison)
            {
                snapshot = await _databaseModelBuilder.BuildSnapshotAsync(subscriptionId, subscription.Database, cancellationToken).ConfigureAwait(false);
                builtNewSnapshot = true;
            }
            else
            {
                snapshot = await _schemaSnapshotRepository.GetLatestForSubscriptionAsync(subscriptionId, cancellationToken).ConfigureAwait(false)
                           ?? await _databaseModelBuilder.BuildSnapshotAsync(subscriptionId, subscription.Database, cancellationToken).ConfigureAwait(false);
                builtNewSnapshot = snapshot.Id == Guid.Empty; // Defensive; current model initializes Id.
            }

            var fileCache = await _fileModelBuilder.BuildCacheAsync(subscriptionId, subscription.Project, cancellationToken).ConfigureAwait(false);

            var differences = await _schemaComparer.CompareAsync(snapshot, fileCache, subscription.Options, cancellationToken).ConfigureAwait(false);

            // Build the set of objects that were discovered but are not
            // supported for comparison (e.g. logins, unknown types). These
            // are exposed for debugging and metrics but never participate in
            // the main diff.
            var unsupportedObjects = BuildUnsupportedObjects(snapshot, fileCache);

            stopwatch.Stop();

            var status = differences.Count == 0 ? ComparisonStatus.Synchronized : ComparisonStatus.HasDifferences;

            var result = new ComparisonResult
            {
                Id = Guid.NewGuid(),
                SubscriptionId = subscriptionId,
                ComparedAt = DateTime.UtcNow,
                Duration = stopwatch.Elapsed,
                Status = status,
                Differences = differences.ToList(),
                Summary = BuildSummary(differences, snapshot, fileCache, subscription.Options),
                UnsupportedObjects = unsupportedObjects
            };

            await _comparisonHistoryRepository.AddAsync(result, cancellationToken).ConfigureAwait(false);

            if (builtNewSnapshot)
            {
                await _schemaSnapshotRepository.AddAsync(snapshot, cancellationToken).ConfigureAwait(false);
            }

            subscription.LastComparedAt = result.ComparedAt;
            await _subscriptionRepository.UpdateAsync(subscription, cancellationToken).ConfigureAwait(false);

            return result;
        }
        finally
        {
            if (acquired)
            {
                semaphore.Release();
            }
        }
    }

    private static ComparisonSummary BuildSummary(
        IReadOnlyList<SchemaDifference> differences,
        SchemaSnapshot dbSnapshot,
        FileModelCache fileCache,
        ComparisonOptions options)
    {
        if (differences is null) throw new ArgumentNullException(nameof(differences));
        if (dbSnapshot is null) throw new ArgumentNullException(nameof(dbSnapshot));
        if (fileCache is null) throw new ArgumentNullException(nameof(fileCache));
        if (options is null) throw new ArgumentNullException(nameof(options));

        var summary = new ComparisonSummary
        {
            TotalDifferences = differences.Count,
            Additions = differences.Count(d => d.DifferenceType == DifferenceType.Add),
            Modifications = differences.Count(d => d.DifferenceType == DifferenceType.Modify),
            Deletions = differences.Count(d => d.DifferenceType == DifferenceType.Delete)
        };

        foreach (var group in differences.GroupBy(d => d.ObjectType.ToString()))
        {
            summary.ByObjectType[group.Key] = group.Count();
        }

        // Build the set of all supported objects that were considered for
        // comparison, while also counting objects that were discovered but
        // intentionally excluded because their types are not in the
        // supported whitelist (for example: logins, unknown types).
        var allKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var obj in dbSnapshot.Objects)
        {
            if (!SqlObjectTypeSupport.IsSupportedForComparison(obj.ObjectType))
            {
                summary.UnsupportedDatabaseObjectCount++;
                continue;
            }

            if (!ShouldInclude(obj.ObjectType, options))
            {
                continue;
            }

            allKeys.Add(BuildKey(obj.ObjectName, obj.ObjectType));
        }

        foreach (var entry in fileCache.FileEntries.Values)
        {
            if (!SqlObjectTypeSupport.IsSupportedForComparison(entry.ObjectType))
            {
                summary.UnsupportedFileObjectCount++;
                continue;
            }

            if (!ShouldInclude(entry.ObjectType, options))
            {
                continue;
            }

            allKeys.Add(BuildKey(entry.ObjectName, entry.ObjectType));
        }

        summary.ObjectsCompared = allKeys.Count;

        if (allKeys.Count == 0)
        {
            summary.ObjectsUnchanged = 0;
            return summary;
        }

        // Each SchemaDifference represents a single object (by type + name).
        // Objects without any difference are "unchanged".
        var differenceKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var difference in differences)
        {
            differenceKeys.Add(BuildKey(difference.ObjectName, difference.ObjectType));
        }

        var changedOrMissingCount = differenceKeys.Count;
        var unchanged = allKeys.Count - changedOrMissingCount;
        if (unchanged < 0)
        {
            unchanged = 0;
        }

        summary.ObjectsUnchanged = unchanged;
        return summary;
    }

    private static string BuildKey(string name, SqlObjectType type) =>
        $"{type}:{name}";

    private static List<UnsupportedObject> BuildUnsupportedObjects(
        SchemaSnapshot dbSnapshot,
        FileModelCache fileCache)
    {
        var results = new List<UnsupportedObject>();

        foreach (var obj in dbSnapshot.Objects)
        {
            if (!SqlObjectTypeSupport.IsSupportedForComparison(obj.ObjectType))
            {
                results.Add(new UnsupportedObject
                {
                    Source = DifferenceSource.Database,
                    ObjectType = obj.ObjectType,
                    SchemaName = obj.SchemaName,
                    ObjectName = obj.ObjectName,
                    FilePath = null
                });
            }
        }

        foreach (var entry in fileCache.FileEntries.Values)
        {
            if (!SqlObjectTypeSupport.IsSupportedForComparison(entry.ObjectType))
            {
                results.Add(new UnsupportedObject
                {
                    Source = DifferenceSource.FileSystem,
                    ObjectType = entry.ObjectType,
                    SchemaName = string.Empty,
                    ObjectName = entry.ObjectName,
                    FilePath = entry.FilePath
                });
            }
        }

        return results;
    }

    private static bool ShouldInclude(SqlObjectType type, ComparisonOptions options) =>
        SqlObjectTypeSupport.IsSupportedForComparison(type) && type switch
        {
            SqlObjectType.Table => options.IncludeTables,
            SqlObjectType.Index => options.IncludeTables,
            SqlObjectType.View => options.IncludeViews,
            SqlObjectType.StoredProcedure => options.IncludeStoredProcedures,
            SqlObjectType.ScalarFunction or SqlObjectType.TableValuedFunction or SqlObjectType.InlineTableValuedFunction => options.IncludeFunctions,
            SqlObjectType.Trigger => options.IncludeTriggers,
            // Database principals are whitelisted and always included when
            // present; there is no per-type toggle in options today.
            SqlObjectType.User or SqlObjectType.Role => true,
            _ => false
        };
}

