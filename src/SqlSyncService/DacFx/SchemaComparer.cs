using SqlSyncService.Domain.Caching;
using SqlSyncService.Domain.Comparisons;

namespace SqlSyncService.DacFx;

public interface ISchemaComparer
{
    Task<IReadOnlyList<SchemaDifference>> CompareAsync(
        SchemaSnapshot dbSnapshot,
        FileModelCache fileCache,
        ComparisonOptions options,
        CancellationToken cancellationToken = default);
}

public class SchemaComparer : ISchemaComparer
{
    public Task<IReadOnlyList<SchemaDifference>> CompareAsync(
        SchemaSnapshot dbSnapshot,
        FileModelCache fileCache,
        ComparisonOptions options,
        CancellationToken cancellationToken = default)
    {
        if (dbSnapshot is null) throw new ArgumentNullException(nameof(dbSnapshot));
        if (fileCache is null) throw new ArgumentNullException(nameof(fileCache));
        if (options is null) throw new ArgumentNullException(nameof(options));

        var differences = new List<SchemaDifference>();

        // Group database objects by logical key (type + name). This allows us to
        // handle scenarios where the same object name exists in multiple schemas
        // (e.g. dbo.Table and archive.Table) without throwing due to duplicate
        // dictionary keys.
        var dbObjects = dbSnapshot.Objects
            .Where(o => ShouldInclude(o.ObjectType, options))
            .GroupBy(o => BuildKey(o.ObjectName, o.ObjectType), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.ToList(),
                StringComparer.OrdinalIgnoreCase);

        // File objects are also grouped by logical key, but unlike the database
        // side we may legitimately have multiple files that represent different
        // schemas for the same object name (e.g. dbo.Table and Staging.Table).
        // Therefore we keep *all* file entries per key and perform pairwise
        // matching against the database group instead of collapsing to a single
        // file.
        var fileObjects = fileCache.FileEntries.Values
            .Where(e => ShouldInclude(e.ObjectType, options))
            .GroupBy(e => BuildKey(e.ObjectName, e.ObjectType), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.ToList(),
                StringComparer.OrdinalIgnoreCase);

        // Work over the union of keys so that we can correctly surface Add/Delete
        // differences when only one side (database or files) has entries for a
        // given logical object.
        var allKeys = new HashSet<string>(dbObjects.Keys, StringComparer.OrdinalIgnoreCase);
        allKeys.UnionWith(fileObjects.Keys);

        foreach (var key in allKeys)
        {
            cancellationToken.ThrowIfCancellationRequested();

            dbObjects.TryGetValue(key, out var dbGroup);
            fileObjects.TryGetValue(key, out var fileGroup);

            dbGroup ??= new List<SchemaObjectSummary>();
            fileGroup ??= new List<FileObjectEntry>();

            // Database has no objects for this key – every file entry is an Add.
            if (dbGroup.Count == 0 && fileGroup.Count > 0)
            {
                foreach (var fileEntry in fileGroup)
                {
                    differences.Add(new SchemaDifference
                    {
                        Id = Guid.NewGuid(),
                        ObjectName = fileEntry.ObjectName,
                        SchemaName = string.Empty,
                        ObjectType = fileEntry.ObjectType,
                        DifferenceType = DifferenceType.Add,
                        Source = DifferenceSource.FileSystem,
                        FilePath = fileEntry.FilePath,
                        DatabaseDefinition = null,
                        FileDefinition = fileEntry.Content
                    });
                }

                continue;
            }

            // Files have no entries for this key – every database object in the
            // group is treated as a Delete.
            if (dbGroup.Count > 0 && fileGroup.Count == 0)
            {
                foreach (var dbObject in dbGroup)
                {
                    differences.Add(new SchemaDifference
                    {
                        Id = Guid.NewGuid(),
                        ObjectName = dbObject.ObjectName,
                        SchemaName = dbObject.SchemaName,
                        ObjectType = dbObject.ObjectType,
                        DifferenceType = DifferenceType.Delete,
                        Source = DifferenceSource.Database,
                        DatabaseDefinition = dbObject.DefinitionScript,
                        FileDefinition = null
                    });
                }

                continue;
            }

            // Both sides have one or more entries for this logical key. We now
            // perform pairwise matching between database objects and file
            // entries. For each file we choose the most appropriate database
            // object using the existing schema-aware helper, removing matches
            // from the pool as we go. Any remaining database objects become
            // Deletes, and any remaining files become Adds.
            var remainingDb = new List<SchemaObjectSummary>(dbGroup);
            var remainingFiles = new List<FileObjectEntry>(fileGroup);

            while (remainingDb.Count > 0 && remainingFiles.Count > 0)
            {
                var fileEntry = remainingFiles[0];
                remainingFiles.RemoveAt(0);

                var primaryDbObject = ChoosePrimaryForFileMatch(remainingDb, fileEntry);

                if (!string.Equals(primaryDbObject.DefinitionHash, fileEntry.ContentHash, StringComparison.Ordinal))
                {
                    differences.Add(new SchemaDifference
                    {
                        Id = Guid.NewGuid(),
                        ObjectName = primaryDbObject.ObjectName,
                        SchemaName = primaryDbObject.SchemaName,
                        ObjectType = primaryDbObject.ObjectType,
                        DifferenceType = DifferenceType.Modify,
                        Source = DifferenceSource.FileSystem,
                        FilePath = fileEntry.FilePath,
                        DatabaseDefinition = primaryDbObject.DefinitionScript,
                        FileDefinition = fileEntry.Content
                    });
                }

                // Mark this database object as matched so that subsequent files
                // for the same logical key are paired with the remaining
                // database objects (if any).
                remainingDb.Remove(primaryDbObject);
            }

            // Any remaining database objects in the group that were not matched
            // to a file entry are considered Deletes.
            foreach (var extra in remainingDb)
            {
                differences.Add(new SchemaDifference
                {
                    Id = Guid.NewGuid(),
                    ObjectName = extra.ObjectName,
                    SchemaName = extra.SchemaName,
                    ObjectType = extra.ObjectType,
                    DifferenceType = DifferenceType.Delete,
                    Source = DifferenceSource.Database,
                    DatabaseDefinition = extra.DefinitionScript,
                    FileDefinition = null
                });
            }

            // Any remaining file entries that were not matched to a database
            // object are treated as Adds. Today we do not attempt to infer a
            // schema for file-only objects, so SchemaName remains empty.
            foreach (var extraFile in remainingFiles)
            {
                differences.Add(new SchemaDifference
                {
                    Id = Guid.NewGuid(),
                    ObjectName = extraFile.ObjectName,
                    SchemaName = string.Empty,
                    ObjectType = extraFile.ObjectType,
                    DifferenceType = DifferenceType.Add,
                    Source = DifferenceSource.FileSystem,
                    FilePath = extraFile.FilePath,
                    DatabaseDefinition = null,
                    FileDefinition = extraFile.Content
                });
            }
        }

        return Task.FromResult<IReadOnlyList<SchemaDifference>>(differences);
    }

    private static string BuildKey(string name, SqlObjectType type) =>
        $"{type}:{name}";

    private static SchemaObjectSummary ChoosePrimaryForFileMatch(
        IReadOnlyList<SchemaObjectSummary> dbGroup,
        FileObjectEntry fileEntry)
    {
        if (dbGroup is null || dbGroup.Count == 0)
        {
            throw new ArgumentException("Group must contain at least one object.", nameof(dbGroup));
        }

        if (dbGroup.Count == 1)
        {
            return dbGroup[0];
        }

        // Try to infer the most appropriate schema for this file based on its
        // path and content. This is important when multiple schemas share the
        // same object name (e.g. dbo.Table and archive.Table) so that we
        // compare against the matching schema when the file clearly targets
        // it (such as CREATE TABLE [DataArchive].[TechnicalOrderData_LineItems]).
        var candidateSchemas = dbGroup
            .Select(o => o.SchemaName)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var inferredSchema = InferSchemaFromFile(fileEntry, candidateSchemas);

        if (!string.IsNullOrWhiteSpace(inferredSchema))
        {
            var schemaMatch = dbGroup.FirstOrDefault(o =>
                string.Equals(o.SchemaName, inferredSchema, StringComparison.OrdinalIgnoreCase));

            if (schemaMatch is not null)
            {
                return schemaMatch;
            }
        }

        // Prefer the default schema when present so that, in common scenarios
        // where files are authored for dbo objects, we compare against the most
        // likely match.
        var dboCandidate = dbGroup.FirstOrDefault(o =>
            string.Equals(o.SchemaName, "dbo", StringComparison.OrdinalIgnoreCase));

        if (dboCandidate is not null)
        {
            return dboCandidate;
        }

        // Fall back to a deterministic choice based on schema name ordering.
        return dbGroup
            .OrderBy(o => o.SchemaName, StringComparer.OrdinalIgnoreCase)
            .First();
    }

    private static string? InferSchemaFromFile(FileObjectEntry fileEntry, IReadOnlyCollection<string> candidateSchemas)
    {
        if (candidateSchemas.Count == 0)
        {
            return null;
        }

        // 1) Try to infer from the file path segments – if any segment matches
        //    a known schema name, prefer that.
        if (!string.IsNullOrWhiteSpace(fileEntry.FilePath))
        {
            var separators = new[] { '/', '\\' };
            var segments = fileEntry.FilePath
                .Split(separators, StringSplitOptions.RemoveEmptyEntries);

            if (segments.Length > 0)
            {
                var schemaSet = new HashSet<string>(candidateSchemas, StringComparer.OrdinalIgnoreCase);

                foreach (var segment in segments)
                {
                    if (schemaSet.Contains(segment))
                    {
                        return segment;
                    }
                }
            }
        }

        // 2) Fall back to looking inside the file content for patterns like
        //    CREATE TABLE [SchemaName].[ObjectName]. We keep this simple and
        //    heuristic on purpose.
        if (!string.IsNullOrWhiteSpace(fileEntry.Content))
        {
            foreach (var candidate in candidateSchemas)
            {
                var pattern = $"[{candidate}].";
                if (fileEntry.Content.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static bool ShouldInclude(SqlObjectType type, ComparisonOptions options)
    {
        if (!SqlObjectTypeSupport.IsSupportedForComparison(type))
        {
            // Anything outside the supported whitelist (including Login,
            // Unknown, etc.) is excluded from comparison.
            return false;
        }

        return type switch
        {
            SqlObjectType.Table => options.IncludeTables,
            // Indexes share the same toggle as tables: when table comparison
            // is enabled, related indexes participate in the diff as well.
            SqlObjectType.Index => options.IncludeTables,
            SqlObjectType.View => options.IncludeViews,
            SqlObjectType.StoredProcedure => options.IncludeStoredProcedures,
            SqlObjectType.ScalarFunction or SqlObjectType.TableValuedFunction or SqlObjectType.InlineTableValuedFunction => options.IncludeFunctions,
            SqlObjectType.Trigger => options.IncludeTriggers,
            // Security principals are whitelisted and always included
            // when present; there is no per-type toggle today.
            SqlObjectType.User or SqlObjectType.Role => true,
            _ => false
        };
    }
}

