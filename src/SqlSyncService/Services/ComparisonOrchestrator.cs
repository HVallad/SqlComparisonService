using System.Diagnostics;
using System.Linq;
using Microsoft.Extensions.Options;
using SqlSyncService.Configuration;
using SqlSyncService.DacFx;
using SqlSyncService.Domain.Caching;
using SqlSyncService.Domain.Changes;
using SqlSyncService.Domain.Comparisons;
using SqlSyncService.Domain.Subscriptions;
using SqlSyncService.Persistence;

namespace SqlSyncService.Services;

public interface IComparisonOrchestrator
{
    Task<ComparisonResult> RunComparisonAsync(Guid subscriptionId, bool fullComparison, CancellationToken cancellationToken = default);

    /// <summary>
    /// Compares a single database object against its file definition.
    /// Always fetches fresh data from the database for the specified object.
    /// </summary>
    Task<SingleObjectComparisonResult> CompareObjectAsync(
        Guid subscriptionId,
        string schemaName,
        string objectName,
        SqlObjectType objectType,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Compares multiple database objects against their file definitions efficiently.
    /// Uses batched queries (one per object type) and incrementally updates the cached snapshot.
    /// Returns a full comparison result showing all differences.
    /// </summary>
    /// <param name="subscriptionId">The subscription ID.</param>
    /// <param name="changedObjects">The objects that changed and need to be re-queried.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A full comparison result including all differences.</returns>
    Task<ComparisonResult> CompareObjectsAsync(
        Guid subscriptionId,
        IEnumerable<ObjectIdentifier> changedObjects,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Result of a single-object comparison.
/// </summary>
public class SingleObjectComparisonResult
{
    public Guid SubscriptionId { get; set; }
    public string SchemaName { get; set; } = string.Empty;
    public string ObjectName { get; set; } = string.Empty;
    public SqlObjectType ObjectType { get; set; }
    public bool ExistsInDatabase { get; set; }
    public bool ExistsInFileSystem { get; set; }
    public bool IsSynchronized { get; set; }
    public SchemaDifference? Difference { get; set; }
    public DateTime ComparedAt { get; set; }
}

public sealed class ComparisonOrchestrator : IComparisonOrchestrator
{
    private static SemaphoreSlim? _semaphore;
    private static readonly object _lock = new();

    private readonly ISubscriptionRepository _subscriptionRepository;
    private readonly ISchemaSnapshotRepository _schemaSnapshotRepository;
    private readonly IComparisonHistoryRepository _comparisonHistoryRepository;
    private readonly IDatabaseModelBuilder _databaseModelBuilder;
    private readonly IDatabaseSchemaReader _schemaReader;
    private readonly IFileModelBuilder _fileModelBuilder;
    private readonly ISchemaComparer _schemaComparer;
    private readonly ILogger<ComparisonOrchestrator> _logger;

    public ComparisonOrchestrator(
        ISubscriptionRepository subscriptionRepository,
        ISchemaSnapshotRepository schemaSnapshotRepository,
        IComparisonHistoryRepository comparisonHistoryRepository,
        IDatabaseModelBuilder databaseModelBuilder,
        IDatabaseSchemaReader schemaReader,
        IFileModelBuilder fileModelBuilder,
        ISchemaComparer schemaComparer,
        IOptions<ServiceConfiguration> options,
        ILogger<ComparisonOrchestrator> logger)
    {
        _subscriptionRepository = subscriptionRepository ?? throw new ArgumentNullException(nameof(subscriptionRepository));
        _schemaSnapshotRepository = schemaSnapshotRepository ?? throw new ArgumentNullException(nameof(schemaSnapshotRepository));
        _comparisonHistoryRepository = comparisonHistoryRepository ?? throw new ArgumentNullException(nameof(comparisonHistoryRepository));
        _databaseModelBuilder = databaseModelBuilder ?? throw new ArgumentNullException(nameof(databaseModelBuilder));
        _schemaReader = schemaReader ?? throw new ArgumentNullException(nameof(schemaReader));
        _fileModelBuilder = fileModelBuilder ?? throw new ArgumentNullException(nameof(fileModelBuilder));
        _schemaComparer = schemaComparer ?? throw new ArgumentNullException(nameof(schemaComparer));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

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
                _logger.LogInformation("Starting full comparison for subscription {SubscriptionId} - building fresh snapshot from database.", subscriptionId);
                snapshot = await _databaseModelBuilder.BuildSnapshotAsync(subscriptionId, subscription.Database, cancellationToken).ConfigureAwait(false);
                builtNewSnapshot = true;
            }
            else
            {
                _logger.LogDebug("Starting non-full comparison for subscription {SubscriptionId} - attempting to use cached snapshot.", subscriptionId);
                var cachedSnapshot = await _schemaSnapshotRepository.GetLatestForSubscriptionAsync(subscriptionId, cancellationToken).ConfigureAwait(false);
                if (cachedSnapshot is not null)
                {
                    _logger.LogInformation(
                        "Found cached snapshot ID {SnapshotId} captured at {CapturedAt} with {ObjectCount} objects for subscription {SubscriptionId}.",
                        cachedSnapshot.Id, cachedSnapshot.CapturedAt, cachedSnapshot.Objects.Count, subscriptionId);
                    snapshot = cachedSnapshot;
                    builtNewSnapshot = false;
                }
                else
                {
                    _logger.LogInformation("No cached snapshot found for subscription {SubscriptionId} - building fresh snapshot from database.", subscriptionId);
                    snapshot = await _databaseModelBuilder.BuildSnapshotAsync(subscriptionId, subscription.Database, cancellationToken).ConfigureAwait(false);
                    builtNewSnapshot = true;
                }
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
                // Delete old snapshots before adding the new one to ensure subsequent
                // non-full comparisons use fresh data with current normalization rules
                _logger.LogInformation(
                    "Full comparison completed for subscription {SubscriptionId}. Deleting old snapshots and saving new snapshot with {ObjectCount} objects.",
                    subscriptionId, snapshot.Objects.Count);
                await _schemaSnapshotRepository.DeleteForSubscriptionAsync(subscriptionId, cancellationToken).ConfigureAwait(false);
                await _schemaSnapshotRepository.AddAsync(snapshot, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("New snapshot saved with ID {SnapshotId} for subscription {SubscriptionId}.", snapshot.Id, subscriptionId);
            }
            else
            {
                _logger.LogDebug(
                    "Using cached snapshot with ID {SnapshotId} (captured at {CapturedAt}) for subscription {SubscriptionId}.",
                    snapshot.Id, snapshot.CapturedAt, subscriptionId);
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

		    public async Task<SingleObjectComparisonResult> CompareObjectAsync(
		        Guid subscriptionId,
		        string schemaName,
		        string objectName,
		        SqlObjectType objectType,
		        CancellationToken cancellationToken = default)
		    {
		        if (subscriptionId == Guid.Empty) throw new ArgumentException("SubscriptionId must not be empty.", nameof(subscriptionId));
		        if (string.IsNullOrWhiteSpace(objectName)) throw new ArgumentException("ObjectName must not be empty.", nameof(objectName));

		        var startTime = DateTime.UtcNow;

		        var semaphore = _semaphore ?? throw new InvalidOperationException("Comparison semaphore is not initialized.");
		        var acquired = false;

		        try
		        {
		            acquired = await semaphore.WaitAsync(0, cancellationToken).ConfigureAwait(false);
		            if (!acquired)
		            {
		                throw new ComparisonInProgressException("A comparison is already in progress. Please try again later.");
		            }

		            _logger.LogInformation(
		                "Starting single-object comparison for {Schema}.{Object} ({Type}) in subscription {SubscriptionId}",
		                schemaName, objectName, objectType, subscriptionId);

		            var subscription = await _subscriptionRepository.GetByIdAsync(subscriptionId, cancellationToken).ConfigureAwait(false)
		                ?? throw new SubscriptionNotFoundException(subscriptionId);

		            // 1. Get or create cached snapshot so we can incrementally update it with the single object
		            var snapshot = await _schemaSnapshotRepository.GetLatestForSubscriptionAsync(subscriptionId, cancellationToken).ConfigureAwait(false);

		            if (snapshot is null)
		            {
		                _logger.LogDebug(
		                    "No cached snapshot found, building full snapshot for subscription {SubscriptionId} before single-object comparison",
		                    subscriptionId);

		                snapshot = await _databaseModelBuilder.BuildSnapshotAsync(subscriptionId, subscription.Database, cancellationToken).ConfigureAwait(false);

		                // Ensure the new snapshot is persisted so subsequent incremental updates work correctly
		                await _schemaSnapshotRepository.DeleteForSubscriptionAsync(subscriptionId, cancellationToken).ConfigureAwait(false);
		                await _schemaSnapshotRepository.AddAsync(snapshot, cancellationToken).ConfigureAwait(false);

		                _logger.LogInformation(
		                    "Single-object comparison created new snapshot ID {SnapshotId} with {ObjectCount} objects for subscription {SubscriptionId}",
		                    snapshot.Id, snapshot.Objects.Count, subscriptionId);
		            }
		            else
		            {
		                _logger.LogInformation(
		                    "Single-object comparison using cached snapshot ID {SnapshotId} captured at {CapturedAt} with {ObjectCount} objects for subscription {SubscriptionId}",
		                    snapshot.Id, snapshot.CapturedAt, snapshot.Objects.Count, subscriptionId);
		            }

		            // 2. Query just the requested object from the database
		            var dbObject = await _schemaReader.GetObjectAsync(
		                subscription.Database,
		                schemaName,
		                objectName,
		                objectType,
		                cancellationToken).ConfigureAwait(false);

		            if (dbObject is not null)
		            {
		                // Update or insert the single object into the cached snapshot
		                await _schemaSnapshotRepository.UpdateObjectsAsync(snapshot.Id, new[] { dbObject }, cancellationToken).ConfigureAwait(false);
		            }
		            else
		            {
		                // Object no longer exists in the database - remove it from the cached snapshot
		                await _schemaSnapshotRepository.RemoveObjectAsync(snapshot.Id, schemaName, objectName, objectType, cancellationToken).ConfigureAwait(false);
		            }

		            // 3. Reload updated snapshot to get the latest state (mirrors CompareObjectsAsync)
		            var originalSnapshotId = snapshot.Id;
		            snapshot = await _schemaSnapshotRepository.GetLatestForSubscriptionAsync(subscriptionId, cancellationToken).ConfigureAwait(false)
		                ?? snapshot; // Defensive fallback

		            if (snapshot.Id != originalSnapshotId)
		            {
		                _logger.LogWarning(
		                    "Snapshot ID changed during single-object comparison! Original: {OriginalId}, New: {NewId}",
		                    originalSnapshotId, snapshot.Id);
		            }

		            _logger.LogDebug(
		                "After single-object update: snapshot ID {SnapshotId} has {ObjectCount} objects",
		                snapshot.Id, snapshot.Objects.Count);

		            // 4. Build file cache and locate the corresponding file object
		            var fileCache = await _fileModelBuilder.BuildCacheAsync(subscriptionId, subscription.Project, cancellationToken).ConfigureAwait(false);

		            _logger.LogDebug(
		                "File cache built with {FileCount} files for subscription {SubscriptionId} during single-object comparison",
		                fileCache.FileEntries.Count, subscriptionId);

		            var fileObject = fileCache.FileEntries.Values.FirstOrDefault(f =>
		                string.Equals(f.ObjectName, objectName, StringComparison.OrdinalIgnoreCase) &&
		                f.ObjectType == objectType);

		            var result = new SingleObjectComparisonResult
		            {
		                SubscriptionId = subscriptionId,
		                SchemaName = schemaName,
		                ObjectName = objectName,
		                ObjectType = objectType,
		                ExistsInDatabase = dbObject != null,
		                ExistsInFileSystem = fileObject != null,
		                ComparedAt = DateTime.UtcNow
		            };

		            // 5. Determine the per-object difference for the requested object
		            if (dbObject == null && fileObject == null)
		            {
		                // Object doesn't exist in either place
		                result.IsSynchronized = true;
		            }
		            else if (dbObject != null && fileObject == null)
		            {
		                // Object exists in database but not in files - Delete difference
		                result.IsSynchronized = false;
		                result.Difference = new SchemaDifference
		                {
		                    Id = Guid.NewGuid(),
		                    ObjectName = objectName,
		                    SchemaName = schemaName,
		                    ObjectType = objectType,
		                    DifferenceType = DifferenceType.Delete,
		                    Source = DifferenceSource.Database,
		                    DatabaseDefinition = dbObject.DefinitionScript,
		                    FileDefinition = null,
		                    FilePath = null
		                };
		            }
		            else if (dbObject == null && fileObject != null)
		            {
		                // Object exists in files but not in database - Add difference
		                result.IsSynchronized = false;
		                result.Difference = new SchemaDifference
		                {
		                    Id = Guid.NewGuid(),
		                    ObjectName = objectName,
		                    SchemaName = schemaName,
		                    ObjectType = objectType,
		                    DifferenceType = DifferenceType.Add,
		                    Source = DifferenceSource.FileSystem,
		                    DatabaseDefinition = null,
		                    FileDefinition = fileObject.Content,
		                    FilePath = fileObject.FilePath
		                };
		            }
		            else if (string.Equals(dbObject!.DefinitionHash, fileObject!.ContentHash, StringComparison.Ordinal))
		            {
		                // Both exist and hashes match - synchronized
		                result.IsSynchronized = true;
		            }
		            else
		            {
		                // Hashes differ - Modify difference
		                result.IsSynchronized = false;
		                result.Difference = new SchemaDifference
		                {
		                    Id = Guid.NewGuid(),
		                    ObjectName = objectName,
		                    SchemaName = schemaName,
		                    ObjectType = objectType,
		                    DifferenceType = DifferenceType.Modify,
		                    Source = DifferenceSource.FileSystem,
		                    DatabaseDefinition = dbObject.DefinitionScript,
		                    FileDefinition = fileObject.Content,
		                    FilePath = fileObject.FilePath
		                };
		            }

		            // 6. Run a full comparison so history/summary reflect the entire subscription
		            var differences = await _schemaComparer.CompareAsync(snapshot, fileCache, subscription.Options, cancellationToken).ConfigureAwait(false);
		            var unsupportedObjects = BuildUnsupportedObjects(snapshot, fileCache);

		            var duration = DateTime.UtcNow - startTime;
		            var status = differences.Count == 0 ? ComparisonStatus.Synchronized : ComparisonStatus.HasDifferences;

		            var comparisonResult = new ComparisonResult
		            {
		                Id = Guid.NewGuid(),
		                SubscriptionId = subscriptionId,
		                ComparedAt = result.ComparedAt,
		                Duration = duration,
		                Status = status,
		                Differences = differences.ToList(),
		                Summary = BuildSummary(differences, snapshot, fileCache, subscription.Options),
		                UnsupportedObjects = unsupportedObjects
		            };

		            await _comparisonHistoryRepository.AddAsync(comparisonResult, cancellationToken).ConfigureAwait(false);

		            subscription.LastComparedAt = result.ComparedAt;
		            await _subscriptionRepository.UpdateAsync(subscription, cancellationToken).ConfigureAwait(false);

		            _logger.LogInformation(
		                "Single-object comparison completed for {Schema}.{Object} in {Duration:N0}ms - {Status}",
		                schemaName, objectName, duration.TotalMilliseconds,
		                status == ComparisonStatus.Synchronized ? "Synchronized" : "HasDifferences");

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

    public async Task<ComparisonResult> CompareObjectsAsync(
        Guid subscriptionId,
        IEnumerable<ObjectIdentifier> changedObjects,
        CancellationToken cancellationToken = default)
    {
        if (subscriptionId == Guid.Empty) throw new ArgumentException("SubscriptionId must not be empty.", nameof(subscriptionId));
        ArgumentNullException.ThrowIfNull(changedObjects);

        var objectList = changedObjects.ToList();
        if (objectList.Count == 0)
        {
            // No changes - just return current state via regular comparison
            return await RunComparisonAsync(subscriptionId, fullComparison: false, cancellationToken).ConfigureAwait(false);
        }

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

            _logger.LogInformation(
                "Starting batched comparison for {Count} changed objects in subscription {SubscriptionId}",
                objectList.Count, subscriptionId);

            // 1. Get or create cached snapshot
            var snapshot = await _schemaSnapshotRepository.GetLatestForSubscriptionAsync(subscriptionId, cancellationToken).ConfigureAwait(false);
            var createdNewSnapshot = false;

            if (snapshot is null)
            {
                _logger.LogDebug("No cached snapshot found, building full snapshot for subscription {SubscriptionId}", subscriptionId);
                snapshot = await _databaseModelBuilder.BuildSnapshotAsync(subscriptionId, subscription.Database, cancellationToken).ConfigureAwait(false);
                createdNewSnapshot = true;
            }
            else
            {
                _logger.LogInformation(
                    "Batched comparison using cached snapshot ID {SnapshotId} captured at {CapturedAt} with {ObjectCount} objects for subscription {SubscriptionId}",
                    snapshot.Id, snapshot.CapturedAt, snapshot.Objects.Count, subscriptionId);
            }

            // 2. Batch query ONLY the changed objects (grouped by type internally)
            var freshObjects = await _schemaReader.GetObjectsAsync(
                subscription.Database,
                objectList,
                cancellationToken).ConfigureAwait(false);

            _logger.LogDebug(
                "Queried {QueriedCount} objects, found {FoundCount} in database",
                objectList.Count, freshObjects.Count);

            // 3. Update the cached snapshot with all fresh objects
            if (freshObjects.Count > 0)
            {
                await _schemaSnapshotRepository.UpdateObjectsAsync(snapshot.Id, freshObjects, cancellationToken).ConfigureAwait(false);
            }

            // 4. Handle deleted objects (queried but not found in database)
            var foundKeys = new HashSet<string>(
                freshObjects.Select(o => $"{o.ObjectType}:{o.SchemaName}.{o.ObjectName}"),
                StringComparer.OrdinalIgnoreCase);

            foreach (var queried in objectList)
            {
                var key = $"{queried.ObjectType}:{queried.SchemaName}.{queried.ObjectName}";
                if (!foundKeys.Contains(key))
                {
                    _logger.LogDebug("Object {Key} not found in database, removing from snapshot", key);
                    await _schemaSnapshotRepository.RemoveObjectAsync(
                        snapshot.Id,
                        queried.SchemaName,
                        queried.ObjectName,
                        queried.ObjectType,
                        cancellationToken).ConfigureAwait(false);
                }
            }

            // 5. Reload updated snapshot to get the latest state
            var originalSnapshotId = snapshot.Id;
            snapshot = await _schemaSnapshotRepository.GetLatestForSubscriptionAsync(subscriptionId, cancellationToken).ConfigureAwait(false)
                ?? snapshot; // Defensive fallback

            if (snapshot.Id != originalSnapshotId)
            {
                _logger.LogWarning(
                    "Snapshot ID changed during batched comparison! Original: {OriginalId}, New: {NewId}",
                    originalSnapshotId, snapshot.Id);
            }

            _logger.LogDebug(
                "After update: snapshot ID {SnapshotId} has {ObjectCount} objects",
                snapshot.Id, snapshot.Objects.Count);

            // 6. Build file cache and run comparison
            var fileCache = await _fileModelBuilder.BuildCacheAsync(subscriptionId, subscription.Project, cancellationToken).ConfigureAwait(false);

            _logger.LogDebug(
                "File cache built with {FileCount} files for subscription {SubscriptionId}",
                fileCache.FileEntries.Count, subscriptionId);

            var differences = await _schemaComparer.CompareAsync(snapshot, fileCache, subscription.Options, cancellationToken).ConfigureAwait(false);
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

            // Store the new snapshot if we created one (otherwise it was already updated in place)
            if (createdNewSnapshot)
            {
                // Delete old snapshots before adding the new one to ensure subsequent
                // comparisons use fresh data with current normalization rules
                await _schemaSnapshotRepository.DeleteForSubscriptionAsync(subscriptionId, cancellationToken).ConfigureAwait(false);
                await _schemaSnapshotRepository.AddAsync(snapshot, cancellationToken).ConfigureAwait(false);
            }

            subscription.LastComparedAt = result.ComparedAt;
            await _subscriptionRepository.UpdateAsync(subscription, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation(
                "Batched comparison completed for {Count} objects in {Duration:N0}ms - {Status} ({DiffCount} differences)",
                objectList.Count, stopwatch.ElapsedMilliseconds,
                status, differences.Count);

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

