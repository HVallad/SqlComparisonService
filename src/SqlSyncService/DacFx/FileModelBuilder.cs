using SqlSyncService.Domain.Caching;
using SqlSyncService.Domain.Comparisons;
using SqlSyncService.Domain.Subscriptions;
using System.Security.Cryptography;
using System.Text;

namespace SqlSyncService.DacFx;

public interface IFileModelBuilder
{
    Task<FileModelCache> BuildCacheAsync(Guid subscriptionId, ProjectFolder folder, CancellationToken cancellationToken = default);
}

public class FileModelBuilder : IFileModelBuilder
{
    public async Task<FileModelCache> BuildCacheAsync(Guid subscriptionId, ProjectFolder folder, CancellationToken cancellationToken = default)
    {
        if (folder is null) throw new ArgumentNullException(nameof(folder));

        if (string.IsNullOrWhiteSpace(folder.RootPath))
        {
            throw new ArgumentException("ProjectFolder.RootPath must be provided.", nameof(folder));
        }

        if (!Directory.Exists(folder.RootPath))
        {
            throw new DirectoryNotFoundException($"Project folder '{folder.RootPath}' does not exist.");
        }

        var cache = new FileModelCache
        {
            SubscriptionId = subscriptionId,
            CapturedAt = DateTime.UtcNow
        };

        var files = Directory.EnumerateFiles(folder.RootPath, "*.sql", SearchOption.AllDirectories);

        foreach (var fullPath in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (IsInExcludedDirectory(fullPath))
            {
                continue;
            }

            var relativePath = Path.GetRelativePath(folder.RootPath, fullPath);
            var lastModified = File.GetLastWriteTimeUtc(fullPath);
            var rawContent = await File.ReadAllTextAsync(fullPath, cancellationToken).ConfigureAwait(false);
            var normalizedContent = SqlScriptNormalizer.Normalize(rawContent);
            var objectType = InferObjectType(normalizedContent);

            // Always store the primary object represented by the file using the
            // relative file path as the dictionary key. This preserves the
            // existing behaviour where there is a single entry per file.
            var effectiveContent = objectType == SqlObjectType.Table
                ? SqlScriptNormalizer.StripInlineConstraints(SqlScriptNormalizer.TruncateAfterFirstGo(normalizedContent))
                : normalizedContent;
            var comparisonContent = SqlScriptNormalizer.NormalizeForComparison(effectiveContent);
            var contentHash = ComputeSha256(Encoding.UTF8.GetBytes(comparisonContent));

            var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(fullPath);
            var baseObjectName = ExtractObjectName(fileNameWithoutExtension);
            string objectName;

            if (objectType == SqlObjectType.Index)
            {
                // For standalone index scripts, derive the object name from the
                // index definition so that it matches the database-side
                // convention of TableName.IndexName. This avoids collisions
                // when multiple tables share the same index name.
                var stripped = StripComments(normalizedContent);
                var indexName = TryExtractIndexName(stripped);
                var tableName = TryExtractIndexTableName(stripped);

                objectName = !string.IsNullOrWhiteSpace(tableName) && !string.IsNullOrWhiteSpace(indexName)
                    ? $"{tableName}.{indexName}"
                    : baseObjectName;
            }
            else
            {
                objectName = baseObjectName;
            }

            var primaryEntry = new FileObjectEntry
            {
                FilePath = relativePath,
                ObjectName = objectName,
                ObjectType = objectType,
                ContentHash = contentHash,
                LastModified = lastModified,
                Content = comparisonContent
            };

            cache.FileEntries[relativePath] = primaryEntry;

            // If this is a table file, we also scan subsequent batches for
            // index definitions and add them as independent index entries so
            // that indexes participate in comparison just like other objects.
            if (objectType == SqlObjectType.Table)
            {
                foreach (var index in ExtractIndexesFromScript(normalizedContent))
                {
                    var indexContentBytes = Encoding.UTF8.GetBytes(index.Script);
                    var indexHash = ComputeSha256(indexContentBytes);
                    var indexKey = BuildIndexCacheKey(relativePath, index.ObjectName);

                    cache.FileEntries[indexKey] = new FileObjectEntry
                    {
                        FilePath = relativePath,
                        ObjectName = index.ObjectName,
                        ObjectType = SqlObjectType.Index,
                        ContentHash = indexHash,
                        LastModified = lastModified,
                        Content = index.Script
                    };
                }
            }
        }

        return cache;
    }

    private static bool IsInExcludedDirectory(string path)
    {
        var segments = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        foreach (var segment in segments)
        {
            if (segment.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("obj", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string BuildIndexCacheKey(string relativePath, string indexName)
    {
        // Use a synthetic key that is stable for a given file + index name
        // while remaining opaque to callers, since consumers of FileEntries
        // key off ObjectName/ObjectType rather than dictionary key.
        return $"{relativePath}::INDEX::{indexName}";
    }

    private static SqlObjectType InferObjectType(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            // Empty or whitespace-only content cannot be reliably classified.
            return SqlObjectType.Unknown;
        }

        // Remove comments so that commented-out DDL or comments containing
        // phrases like "CREATE TABLE" do not affect classification.
        var stripped = StripComments(sql);
        var lower = stripped.ToLowerInvariant();

        var patterns = new (string Pattern, SqlObjectType Type)[]
        {
                    ("create or alter function", SqlObjectType.ScalarFunction),
                    ("create function",        SqlObjectType.ScalarFunction),
                    ("alter function",         SqlObjectType.ScalarFunction),
                    ("create or alter procedure", SqlObjectType.StoredProcedure),
                    ("create or alter proc",      SqlObjectType.StoredProcedure),
                    ("create procedure",          SqlObjectType.StoredProcedure),
                    ("create proc",               SqlObjectType.StoredProcedure),
                    ("alter procedure",           SqlObjectType.StoredProcedure),
                    ("alter proc",                SqlObjectType.StoredProcedure),
                    ("create or alter view",      SqlObjectType.View),
                    ("create view",               SqlObjectType.View),
                    ("alter view",                SqlObjectType.View),
                    ("create or alter trigger",   SqlObjectType.Trigger),
                    ("create trigger",            SqlObjectType.Trigger),
                    ("alter trigger",             SqlObjectType.Trigger),
		            // Security principals
		            ("create or alter login",     SqlObjectType.Login),
                    ("create login",              SqlObjectType.Login),
                    ("alter login",               SqlObjectType.Login),
                    ("create or alter role",      SqlObjectType.Role),
                    ("create role",               SqlObjectType.Role),
                    ("alter role",                SqlObjectType.Role),
                    ("create server role",        SqlObjectType.Role),
                    ("alter server role",         SqlObjectType.Role),
                    ("create or alter user",      SqlObjectType.User),
                    ("create user",               SqlObjectType.User),
                    ("alter user",                SqlObjectType.User),
		            // Indexes
		            ("create unique clustered index", SqlObjectType.Index),
                    ("create unique nonclustered index", SqlObjectType.Index),
                    ("create unique index",       SqlObjectType.Index),
                    ("create clustered index",    SqlObjectType.Index),
                    ("create nonclustered index", SqlObjectType.Index),
                    ("create index",              SqlObjectType.Index),
		            // Tables last, so other object types win when they appear earlier
		            ("create or alter table",     SqlObjectType.Table),
                    ("create table",              SqlObjectType.Table),
                    ("alter table",               SqlObjectType.Table)
        };

        var bestIndex = int.MaxValue;
        var bestType = SqlObjectType.Unknown; // default to Unknown when no patterns match

        foreach (var (pattern, type) in patterns)
        {
            var idx = lower.IndexOf(pattern, StringComparison.Ordinal);
            if (idx >= 0 && idx < bestIndex)
            {
                bestIndex = idx;
                bestType = type;
            }
        }

        return bestType;
    }

    private static bool Contains(string sql, string value) =>
        sql.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;

    private static string StripComments(string sql)
    {
        if (string.IsNullOrEmpty(sql))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(sql.Length);
        var i = 0;
        while (i < sql.Length)
        {
            // Block comment: /* ... */
            if (i < sql.Length - 1 && sql[i] == '/' && sql[i + 1] == '*')
            {
                i += 2;
                while (i < sql.Length - 1)
                {
                    if (sql[i] == '*' && sql[i + 1] == '/')
                    {
                        i += 2;
                        break;
                    }
                    i++;
                }
                continue;
            }

            // Line comment: -- ... (until end of line)
            if (i < sql.Length - 1 && sql[i] == '-' && sql[i + 1] == '-')
            {
                i += 2;
                while (i < sql.Length && sql[i] != '\n' && sql[i] != '\r')
                {
                    i++;
                }
                continue;
            }

            sb.Append(sql[i]);
            i++;
        }

        return sb.ToString();
    }

    private static IEnumerable<(string ObjectName, string Script)> ExtractIndexesFromScript(string normalizedSql)
    {
        if (string.IsNullOrWhiteSpace(normalizedSql))
        {
            yield break;
        }

        // Split into batches using the same GO semantics as other
        // normalization helpers, then scan all batches after the first for
        // CREATE INDEX statements.
        var batches = SqlScriptNormalizer.SplitBatches(normalizedSql).ToList();
        if (batches.Count <= 1)
        {
            yield break;
        }

        foreach (var batch in batches.Skip(1))
        {
            var stripped = StripComments(batch);
            if (string.IsNullOrWhiteSpace(stripped))
            {
                continue;
            }

            var lower = stripped.ToLowerInvariant();
            if (!lower.Contains("create") || !lower.Contains("index"))
            {
                continue;
            }

            var indexName = TryExtractIndexName(stripped);
            if (string.IsNullOrEmpty(indexName))
            {
                continue;
            }

            var tableName = TryExtractIndexTableName(stripped);
            var objectName = !string.IsNullOrWhiteSpace(tableName)
                ? $"{tableName}.{indexName}"
                : indexName;

            yield return (objectName, SqlScriptNormalizer.Normalize(batch));
        }
    }

    private static string TryExtractIndexName(string sql)
    {
        // Very lightweight extraction that looks for patterns like
        //   CREATE [UNIQUE] [CLUSTERED|NONCLUSTERED] INDEX [name]
        // and returns the bracketed or bare identifier following INDEX.
        var tokens = sql.Split(new[] { '\r', '\n', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < tokens.Length; i++)
        {
            if (!tokens[i].Equals("index", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (i + 1 >= tokens.Length)
            {
                break;
            }

            var candidate = tokens[i + 1].Trim();
            if (candidate.StartsWith("[", StringComparison.Ordinal) && candidate.EndsWith("]", StringComparison.Ordinal))
            {
                candidate = candidate.Substring(1, candidate.Length - 2);
            }

            return candidate;
        }

        return string.Empty;
    }

    private static string TryExtractIndexTableName(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            return string.Empty;
        }

        var tokens = sql.Split(new[] { '\r', '\n', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < tokens.Length; i++)
        {
            if (!tokens[i].Equals("on", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (i + 1 >= tokens.Length)
            {
                break;
            }

            var candidate = tokens[i + 1].Trim();
            if (candidate.Length == 0)
            {
                continue;
            }

            // Remove any column list starting at the first opening parenthesis.
            var parenIndex = candidate.IndexOf('(');
            if (parenIndex >= 0)
            {
                candidate = candidate.Substring(0, parenIndex);
            }

            candidate = candidate.TrimEnd(';');

            // For names like [schema].[Table] or schema.Table, take the last
            // identifier as the table name.
            var lastDot = candidate.LastIndexOf('.');
            var tablePart = lastDot >= 0 ? candidate[(lastDot + 1)..] : candidate;

            if (tablePart.StartsWith("[", StringComparison.Ordinal) && tablePart.EndsWith("]", StringComparison.Ordinal) && tablePart.Length > 2)
            {
                tablePart = tablePart.Substring(1, tablePart.Length - 2);
            }

            return tablePart;
        }

        return string.Empty;
    }

    private static string ExtractObjectName(string fileNameWithoutExtension)
    {
        if (string.IsNullOrWhiteSpace(fileNameWithoutExtension))
        {
            return string.Empty;
        }

        var trimmed = fileNameWithoutExtension.Trim();
        var parts = trimmed.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return trimmed;
        }

        // For names like "dbo.Table" or "Database.dbo.Table", use the last identifier
        return parts[^1];
    }

    private static string ComputeSha256(byte[] data)
    {
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(data);
        return Convert.ToHexString(hash);
    }
}

