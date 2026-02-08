using Microsoft.Extensions.Logging;
using SqlSyncService.Domain.Caching;
using SqlSyncService.Domain.Comparisons;
using SqlSyncService.DacFx;

namespace SqlSyncService.Persistence;

public class SchemaSnapshotRepository : ISchemaSnapshotRepository
{
    private readonly LiteDbContext _context;
    private readonly ILogger<SchemaSnapshotRepository> _logger;

    public SchemaSnapshotRepository(LiteDbContext context, ILogger<SchemaSnapshotRepository> logger)
    {
        _context = context;
        _logger = logger;
    }

    public Task AddAsync(SchemaSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        if (snapshot.Id == Guid.Empty)
        {
            snapshot.Id = Guid.NewGuid();
        }

        _context.SchemaSnapshots.Insert(snapshot);
        return Task.CompletedTask;
    }

    public Task<SchemaSnapshot?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var result = _context.SchemaSnapshots.FindById(id);
        if (result is not null)
        {
            NormalizeLegacyObjectsIfNeeded(result);
        }

        return Task.FromResult<SchemaSnapshot?>(result);
    }

    public Task<IReadOnlyList<SchemaSnapshot>> GetBySubscriptionAsync(Guid subscriptionId, CancellationToken cancellationToken = default)
    {
        var results = _context.SchemaSnapshots
            .Find(s => s.SubscriptionId == subscriptionId)
            .OrderBy(s => s.CapturedAt)
            .ToList();

        // Normalize legacy objects in all snapshots to ensure consistent schema/name format
        foreach (var snapshot in results)
        {
            NormalizeLegacyObjectsIfNeeded(snapshot);
        }

        return Task.FromResult<IReadOnlyList<SchemaSnapshot>>(results);
    }

    public Task<SchemaSnapshot?> GetLatestForSubscriptionAsync(Guid subscriptionId, CancellationToken cancellationToken = default)
    {
        var result = _context.SchemaSnapshots
            .Find(s => s.SubscriptionId == subscriptionId)
            .OrderByDescending(s => s.CapturedAt)
            .FirstOrDefault();

        if (result is not null)
        {
            NormalizeLegacyObjectsIfNeeded(result);
        }

        return Task.FromResult<SchemaSnapshot?>(result);
    }

    public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var deleted = _context.SchemaSnapshots.Delete(id);
        return Task.FromResult(deleted);
    }

    public Task DeleteForSubscriptionAsync(Guid subscriptionId, CancellationToken cancellationToken = default)
    {
        _context.SchemaSnapshots.DeleteMany(s => s.SubscriptionId == subscriptionId);
        return Task.CompletedTask;
    }

    public Task<int> DeleteOlderThanAsync(DateTime cutoffDate, CancellationToken cancellationToken = default)
    {
        var deleted = _context.SchemaSnapshots.DeleteMany(s => s.CapturedAt < cutoffDate);
        return Task.FromResult(deleted);
    }

    public Task<int> DeleteExcessForSubscriptionAsync(Guid subscriptionId, int maxCount, CancellationToken cancellationToken = default)
    {
        // Get all snapshots for the subscription ordered by date (newest first)
        var snapshots = _context.SchemaSnapshots
            .Find(s => s.SubscriptionId == subscriptionId)
            .OrderByDescending(s => s.CapturedAt)
            .ToList();

        if (snapshots.Count <= maxCount)
        {
            return Task.FromResult(0);
        }

        // Delete excess snapshots (keeping the most recent ones)
        var toDelete = snapshots.Skip(maxCount).Select(s => s.Id).ToList();
        var deletedCount = 0;

        foreach (var id in toDelete)
        {
            if (_context.SchemaSnapshots.Delete(id))
            {
                deletedCount++;
            }
        }

        return Task.FromResult(deletedCount);
    }

    public Task UpdateObjectsAsync(Guid snapshotId, IEnumerable<SchemaObjectSummary> updatedObjects, CancellationToken cancellationToken = default)
    {
        var snapshot = _context.SchemaSnapshots.FindById(snapshotId);
        if (snapshot is null)
        {
            return Task.CompletedTask;
        }

        var objectCountBefore = snapshot.Objects.Count;

        // Normalize legacy objects (if needed) to ensure consistent schema/name
        // format for matching and migrate any truly legacy snapshots to the
        // current normalization pipeline. For snapshots created with the
        // current pipeline this is a no-op, which avoids double-normalizing
        // definition scripts and changing hashes unexpectedly.
        NormalizeLegacyObjectsIfNeeded(snapshot);

        foreach (var updatedObject in updatedObjects)
        {
            // Remove existing object with same identity (schema.name + type)
            // Use helper that handles both new format (separate schema/name) and legacy format (schema in ObjectName)
            var existingIndex = FindObjectIndex(snapshot.Objects, updatedObject.SchemaName, updatedObject.ObjectName, updatedObject.ObjectType);

            if (existingIndex >= 0)
            {
                var existingObj = snapshot.Objects[existingIndex];
                _logger.LogInformation(
                    "UpdateObjectsAsync: Found existing object at index {Index}: Schema='{Schema}', Name='{Name}', Type={Type}",
                    existingIndex, existingObj.SchemaName, existingObj.ObjectName, existingObj.ObjectType);
                snapshot.Objects.RemoveAt(existingIndex);
            }
            else
            {
                _logger.LogError(
                    "UpdateObjectsAsync: NO MATCH for: Schema='{Schema}', Name='{Name}', Type={Type}",
                    updatedObject.SchemaName, updatedObject.ObjectName, updatedObject.ObjectType);
            }

            // Add the updated object
            snapshot.Objects.Add(updatedObject);
        }

        var objectCountAfter = snapshot.Objects.Count;
        _logger.LogInformation(
            "UpdateObjectsAsync: Object count: before={Before}, after={After}, delta={Delta}",
            objectCountBefore, objectCountAfter, objectCountAfter - objectCountBefore);

        // Update the snapshot hash and timestamp
        snapshot.CapturedAt = DateTime.UtcNow;
        snapshot.Hash = ComputeSnapshotHash(snapshot.Objects);

        _context.SchemaSnapshots.Update(snapshot);
        return Task.CompletedTask;
    }

    public Task RemoveObjectAsync(Guid snapshotId, string schemaName, string objectName, SqlObjectType objectType, CancellationToken cancellationToken = default)
    {
        var snapshot = _context.SchemaSnapshots.FindById(snapshotId);
        if (snapshot is null)
        {
            return Task.CompletedTask;
        }

        // Normalize legacy objects (if needed) to ensure consistent schema/name
        // format for matching.
        NormalizeLegacyObjectsIfNeeded(snapshot);

        // Use helper that handles both new format (separate schema/name) and legacy format (schema in ObjectName)
        var existingIndex = FindObjectIndex(snapshot.Objects, schemaName, objectName, objectType);

        if (existingIndex >= 0)
        {
            snapshot.Objects.RemoveAt(existingIndex);

            // Update the snapshot hash and timestamp
            snapshot.CapturedAt = DateTime.UtcNow;
            snapshot.Hash = ComputeSnapshotHash(snapshot.Objects);

            _context.SchemaSnapshots.Update(snapshot);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Finds the index of an object in the list, handling both new format (separate schema/name fields)
    /// and legacy format (schema prefix embedded in ObjectName like "dbo.MyObject").
    ///
    /// For function types we intentionally collapse ScalarFunction, TableValuedFunction and
    /// InlineTableValuedFunction into a single logical bucket so that a change in how a
    /// function is classified does not result in duplicate snapshot entries. This keeps
    /// the identity semantics aligned with SchemaComparer.BuildKey.
    /// </summary>
    private static int FindObjectIndex(List<SchemaObjectSummary> objects, string schemaName, string objectName, SqlObjectType objectType)
    {
        // Normalize the requested type for matching (functions are treated as a single logical type)
        var searchType = NormalizeFunctionType(objectType);

        // Build the fully qualified name to check against legacy format
        var fullName = !string.IsNullOrWhiteSpace(schemaName)
            ? $"{schemaName}.{objectName}"
            : objectName;

        for (var i = 0; i < objects.Count; i++)
        {
            var obj = objects[i];
            var existingType = NormalizeFunctionType(obj.ObjectType);
            if (existingType != searchType)
            {
                continue;
            }

            // Check new format: separate schema and name fields
            if (string.Equals(obj.SchemaName, schemaName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(obj.ObjectName, objectName, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }

            // Check legacy format: schema might be embedded in ObjectName (e.g., "dbo.MyObject")
            // This handles snapshots created before the DacFx replacement that stored schema-qualified names
            if (string.IsNullOrWhiteSpace(obj.SchemaName) &&
                string.Equals(obj.ObjectName, fullName, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }

            // Also check if the existing object has schema in ObjectName and we're looking for just the name
            // e.g., existing: ObjectName="dbo.fn_GetDashboardInfo", looking for: schemaName="dbo", objectName="fn_GetDashboardInfo"
            if (!string.IsNullOrWhiteSpace(obj.ObjectName) && obj.ObjectName.Contains('.'))
            {
                var parts = obj.ObjectName.Split('.', 2);
                if (parts.Length == 2 &&
                    string.Equals(parts[0], schemaName, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(parts[1], objectName, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }
        }

        return -1;
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
        _context.SchemaSnapshots.Update(snapshot);
        return Task.CompletedTask;
    }

    private static string ComputeSnapshotHash(List<SchemaObjectSummary> objects)
    {
        if (objects.Count == 0)
        {
            return string.Empty;
        }

        // Combine all object hashes in a deterministic order
        var sortedHashes = objects
            .OrderBy(o => o.ObjectType.ToString())
            .ThenBy(o => o.SchemaName)
            .ThenBy(o => o.ObjectName)
            .Select(o => o.DefinitionHash);

        var combined = string.Join("|", sortedHashes);
        var bytes = System.Text.Encoding.UTF8.GetBytes(combined);
        var hash = System.Security.Cryptography.SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    /// <summary>
    /// Ensures that a snapshot has been migrated to the current normalization
    /// pipeline. This method is idempotent and will only run the (potentially
    /// expensive) legacy normalization once per snapshot version.
    ///
    /// Snapshots created with <see cref="SchemaSnapshot.CurrentNormalizationVersion"/>
    /// are treated as already normalized and will not have their definition
    /// scripts or hashes altered on load, which prevents double-normalization
    /// from drifting hashes away from the file-side pipeline.
    /// </summary>
    private void NormalizeLegacyObjectsIfNeeded(SchemaSnapshot snapshot)
    {
        if (snapshot is null)
        {
            return;
        }

        // If the snapshot has already been normalized with the current
        // pipeline version, there's nothing to do.
        if (snapshot.NormalizationVersion >= SchemaSnapshot.CurrentNormalizationVersion)
        {
            return;
        }

        NormalizeLegacyObjects(snapshot);
        snapshot.NormalizationVersion = SchemaSnapshot.CurrentNormalizationVersion;
        _context.SchemaSnapshots.Update(snapshot);
    }

    /// <summary>
    /// Normalizes legacy objects where the schema was embedded in ObjectName.
    /// Legacy format: SchemaName = "", ObjectName = "dbo.fn_GetDashboardInfo"
    /// New format: SchemaName = "dbo", ObjectName = "fn_GetDashboardInfo"
    /// Also handles hybrid format where both SchemaName and schema-qualified ObjectName are present.
    /// In addition to normalizing names and removing duplicate function entries, this also
    /// re-normalizes definition scripts and recomputes per-object hashes so that snapshots
    /// created with older comparison logic are brought up-to-date.
    /// </summary>
    private static void NormalizeLegacyObjects(SchemaSnapshot snapshot)
    {
        if (snapshot.Objects is null || snapshot.Objects.Count == 0)
        {
            return;
        }

        foreach (var obj in snapshot.Objects)
        {
            // Skip Index objects - they use TableName.IndexName format, not schema.name
            if (obj.ObjectType == SqlObjectType.Index)
            {
                continue;
            }

            // Check if ObjectName contains a schema prefix (e.g., "dbo.fn_GetDashboardInfo" or "tSQLt.Private_ScriptIndex")
            var dotIndex = obj.ObjectName.IndexOf('.');
            if (dotIndex > 0 && dotIndex < obj.ObjectName.Length - 1)
            {
                var schemaFromName = obj.ObjectName.Substring(0, dotIndex);
                var nameOnly = obj.ObjectName.Substring(dotIndex + 1);

                // If SchemaName is empty, extract from ObjectName (legacy format)
                if (string.IsNullOrWhiteSpace(obj.SchemaName))
                {
                    obj.SchemaName = schemaFromName;
                    obj.ObjectName = nameOnly;
                }
                // If SchemaName is populated but ObjectName still has schema prefix (hybrid format),
                // remove the prefix from ObjectName to ensure consistency
                else if (string.Equals(obj.SchemaName, schemaFromName, StringComparison.OrdinalIgnoreCase))
                {
                    obj.ObjectName = nameOnly;
                }
                // If SchemaName differs from the prefix in ObjectName, keep SchemaName and remove prefix
                else
                {
                    obj.ObjectName = nameOnly;
                }
            }
        }

        // After normalizing names, ensure we don't have duplicate entries for the same
        // logical function when its classification has changed (e.g. scalar vs table-valued).
        RemoveDuplicateFunctionEntries(snapshot);

        // Finally, normalize definition scripts and recompute hashes so that even
        // snapshots created before new normalization rules (e.g. temporal HIDDEN
        // columns) participate in comparisons using the current normalization pipeline.
        NormalizeDefinitionsAndHashes(snapshot);
    }

    private static void RemoveDuplicateFunctionEntries(SchemaSnapshot snapshot)
    {
        if (snapshot.Objects is null || snapshot.Objects.Count == 0)
        {
            return;
        }

        var functionGroups = snapshot.Objects
            .Where(o => o.ObjectType == SqlObjectType.ScalarFunction ||
                        o.ObjectType == SqlObjectType.TableValuedFunction ||
                        o.ObjectType == SqlObjectType.InlineTableValuedFunction)
            .GroupBy(o => $"{o.SchemaName}.{o.ObjectName}", StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var group in functionGroups)
        {
            var duplicates = group.ToList();
            if (duplicates.Count <= 1)
            {
                continue;
            }

            // Keep the most recently modified entry (or the one with a definition if dates are missing)
            var toKeep = duplicates
                .OrderByDescending(o => o.ModifiedDate ?? DateTime.MinValue)
                .ThenByDescending(o => !string.IsNullOrEmpty(o.DefinitionScript))
                .First();

            foreach (var obj in duplicates)
            {
                if (!ReferenceEquals(obj, toKeep))
                {
                    snapshot.Objects.Remove(obj);
                }
            }
        }
    }

    private static void NormalizeDefinitionsAndHashes(SchemaSnapshot snapshot)
    {
        if (snapshot.Objects is null || snapshot.Objects.Count == 0)
        {
            return;
        }

        foreach (var obj in snapshot.Objects)
        {
            if (string.IsNullOrWhiteSpace(obj.DefinitionScript))
            {
                // Nothing to normalize – some objects may not have script-based definitions.
                continue;
            }

            string normalizedDefinition;
            if (obj.ObjectType == SqlObjectType.Index)
            {
                // Index definitions use a dedicated normalization pipeline that
                // collapses whitespace differently from general DDL scripts.
                normalizedDefinition = SqlScriptNormalizer.NormalizeIndexForComparison(obj.DefinitionScript);
            }
            else
            {
                normalizedDefinition = SqlScriptNormalizer.NormalizeForComparison(obj.DefinitionScript);
            }

            obj.DefinitionScript = normalizedDefinition;
            obj.DefinitionHash = ComputeDefinitionHash(normalizedDefinition);
        }

        // After per-object hashes are refreshed, recompute snapshot-level hash.
        snapshot.Hash = ComputeSnapshotHash(snapshot.Objects);
    }

    private static string ComputeDefinitionHash(string normalizedDefinition)
    {
        if (string.IsNullOrEmpty(normalizedDefinition))
        {
            return string.Empty;
        }

        var bytes = System.Text.Encoding.UTF8.GetBytes(normalizedDefinition);
        var hash = System.Security.Cryptography.SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }
}
