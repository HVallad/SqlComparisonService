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

        var dbObjects = dbSnapshot.Objects
            .Where(o => ShouldInclude(o.ObjectType, options))
            .ToDictionary(
                o => BuildKey(o.ObjectName, o.ObjectType),
                o => o,
                StringComparer.OrdinalIgnoreCase);

        var fileObjects = fileCache.FileEntries.Values
            .Where(e => ShouldInclude(e.ObjectType, options))
            .ToDictionary(
                e => BuildKey(e.ObjectName, e.ObjectType),
                e => e,
                StringComparer.OrdinalIgnoreCase);

        foreach (var kvp in dbObjects)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var key = kvp.Key;
            var dbObject = kvp.Value;

            if (!fileObjects.TryGetValue(key, out var fileEntry))
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
            else if (!string.Equals(dbObject.DefinitionHash, fileEntry.ContentHash, StringComparison.Ordinal))
            {
                differences.Add(new SchemaDifference
                {
                    Id = Guid.NewGuid(),
                    ObjectName = dbObject.ObjectName,
                    SchemaName = dbObject.SchemaName,
                    ObjectType = dbObject.ObjectType,
                    DifferenceType = DifferenceType.Modify,
                    Source = DifferenceSource.FileSystem,
	                    FilePath = fileEntry.FilePath,
	                    DatabaseDefinition = dbObject.DefinitionScript,
	                    FileDefinition = fileEntry.Content
                });
            }
        }

        foreach (var kvp in fileObjects)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var key = kvp.Key;
            var fileEntry = kvp.Value;

            if (!dbObjects.ContainsKey(key))
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
        }

        return Task.FromResult<IReadOnlyList<SchemaDifference>>(differences);
    }

    private static string BuildKey(string name, SqlObjectType type) =>
        $"{type}:{name}";

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

