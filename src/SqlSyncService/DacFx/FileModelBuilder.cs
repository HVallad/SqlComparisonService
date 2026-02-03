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
            //
            // For certain object types, we need to truncate at the first GO:
            // - Tables: To exclude indexes/triggers/permissions defined in subsequent batches
            // - Roles: To exclude ALTER ROLE ... ADD MEMBER statements after the CREATE ROLE
            // - Triggers: To exclude ENABLE/DISABLE TRIGGER statements
            string effectiveContent;
            if (objectType == SqlObjectType.Table)
            {
                effectiveContent = SqlScriptNormalizer.StripInlineConstraints(SqlScriptNormalizer.TruncateAfterFirstGo(normalizedContent));
            }
            else if (objectType == SqlObjectType.Role || objectType == SqlObjectType.Trigger)
            {
                effectiveContent = SqlScriptNormalizer.TruncateAfterFirstGo(normalizedContent);
            }
            else
            {
                effectiveContent = normalizedContent;
            }
            var comparisonContent = SqlScriptNormalizer.NormalizeForComparison(effectiveContent);
            var contentHash = ComputeSha256(Encoding.UTF8.GetBytes(comparisonContent));

            var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(fullPath);
            var baseObjectName = ExtractObjectName(fileNameWithoutExtension);
            var stripped = StripComments(normalizedContent);

            // Extract object name from the DDL statement itself rather than relying
            // solely on the filename. This ensures that object names containing dots
            // (e.g., [Schema].[Audit.TableName]) are correctly preserved.
            var objectName = TryExtractObjectNameForType(stripped, objectType, baseObjectName);

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
                    // Apply the same whitespace canonicalization as the primary object
                    // to ensure consistent comparison with the database side.
                    var indexComparisonContent = SqlScriptNormalizer.NormalizeForComparison(index.Script);
                    var indexContentBytes = Encoding.UTF8.GetBytes(indexComparisonContent);
                    var indexHash = ComputeSha256(indexContentBytes);
                    var indexKey = BuildIndexCacheKey(relativePath, index.ObjectName);

                    cache.FileEntries[indexKey] = new FileObjectEntry
                    {
                        FilePath = relativePath,
                        ObjectName = index.ObjectName,
                        ObjectType = SqlObjectType.Index,
                        ContentHash = indexHash,
                        LastModified = lastModified,
                        Content = indexComparisonContent
                    };
                }

                // Also scan subsequent batches for trigger definitions and add
                // them as independent trigger entries so that triggers embedded
                // in table files participate in comparison just like other objects.
                foreach (var trigger in ExtractTriggersFromScript(normalizedContent))
                {
                    // Apply the same whitespace canonicalization as the primary object
                    // to ensure consistent comparison with the database side.
                    var triggerComparisonContent = SqlScriptNormalizer.NormalizeForComparison(trigger.Script);
                    var triggerContentBytes = Encoding.UTF8.GetBytes(triggerComparisonContent);
                    var triggerHash = ComputeSha256(triggerContentBytes);
                    var triggerKey = BuildTriggerCacheKey(relativePath, trigger.ObjectName);

                    cache.FileEntries[triggerKey] = new FileObjectEntry
                    {
                        FilePath = relativePath,
                        ObjectName = trigger.ObjectName,
                        ObjectType = SqlObjectType.Trigger,
                        ContentHash = triggerHash,
                        LastModified = lastModified,
                        Content = triggerComparisonContent
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

    private static string BuildTriggerCacheKey(string relativePath, string triggerName)
    {
        // Use a synthetic key that is stable for a given file + trigger name
        // while remaining opaque to callers, since consumers of FileEntries
        // key off ObjectName/ObjectType rather than dictionary key.
        return $"{relativePath}::TRIGGER::{triggerName}";
    }

    internal static SqlObjectType InferObjectType(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            // Empty or whitespace-only content cannot be reliably classified.
            return SqlObjectType.Unknown;
        }

        // Remove comments so that commented-out DDL or comments containing
        // phrases like "CREATE TABLE" do not affect classification.
        var stripped = StripComments(sql);

        // Note: Functions use a placeholder type here; we refine it below based on RETURNS clause.
        var patterns = new (string Pattern, SqlObjectType Type)[]
        {
            ("create or alter function", SqlObjectType.ScalarFunction),
            ("create function", SqlObjectType.ScalarFunction),
            ("alter function", SqlObjectType.ScalarFunction),
            ("create or alter procedure", SqlObjectType.StoredProcedure),
            ("create or alter proc", SqlObjectType.StoredProcedure),
            ("create procedure", SqlObjectType.StoredProcedure),
            ("create proc", SqlObjectType.StoredProcedure),
            ("alter procedure", SqlObjectType.StoredProcedure),
            ("alter proc", SqlObjectType.StoredProcedure),
            ("create or alter view", SqlObjectType.View),
            ("create view", SqlObjectType.View),
            ("alter view", SqlObjectType.View),
            ("create or alter trigger", SqlObjectType.Trigger),
            ("create trigger", SqlObjectType.Trigger),
            ("alter trigger", SqlObjectType.Trigger),
            // Security principals
            ("create or alter login", SqlObjectType.Login),
            ("create login", SqlObjectType.Login),
            ("alter login", SqlObjectType.Login),
            // Note: ALTER ROLE is intentionally excluded because it's used for membership
            // modifications (e.g., ALTER ROLE [db_owner] ADD MEMBER [user]), not role definitions.
            ("create or alter role", SqlObjectType.Role),
            ("create role", SqlObjectType.Role),
            ("create server role", SqlObjectType.Role),
            ("create or alter user", SqlObjectType.User),
            ("create user", SqlObjectType.User),
            ("alter user", SqlObjectType.User),
            // Indexes
            ("create unique clustered index", SqlObjectType.Index),
            ("create unique nonclustered index", SqlObjectType.Index),
            ("create unique index", SqlObjectType.Index),
            ("create clustered index", SqlObjectType.Index),
            ("create nonclustered index", SqlObjectType.Index),
            ("create index", SqlObjectType.Index),
            // Tables last, so other object types win when they appear earlier
            ("create or alter table", SqlObjectType.Table),
            ("create table", SqlObjectType.Table),
            ("alter table", SqlObjectType.Table)
        };

        var bestIndex = int.MaxValue;
        var bestType = SqlObjectType.Unknown; // default to Unknown when no patterns match

        foreach (var (pattern, type) in patterns)
        {
            // Use flexible whitespace matching so patterns like "create procedure"
            // match SQL with variable whitespace like "CREATE   PROCEDURE"
            var match = TryFindPatternWithFlexibleWhitespace(stripped, pattern);
            if (match >= 0)
            {
                // match is the end position, we need the start position for comparison
                // For type inference, we want the earliest match, so we calculate start
                var startIdx = match - EstimatePatternMatchLength(stripped, pattern, match);
                if (startIdx >= 0 && startIdx < bestIndex)
                {
                    bestIndex = startIdx;
                    bestType = type;
                }
            }
        }

        // For functions, refine the type based on the RETURNS clause.
        // DacFx distinguishes between ScalarFunction and TableValuedFunction,
        // so we need to match that classification for comparison keys to align.
        if (bestType == SqlObjectType.ScalarFunction)
        {
            bestType = InferFunctionType(stripped);
        }

        return bestType;
    }

    /// <summary>
    /// Determines the specific function type based on the RETURNS clause.
    /// - RETURNS TABLE ... AS RETURN → InlineTableValuedFunction (but DacFx reports as TableValuedFunction)
    /// - RETURNS @var TABLE (...) → Multi-statement TableValuedFunction
    /// - RETURNS scalar_type → ScalarFunction
    /// </summary>
    private static SqlObjectType InferFunctionType(string sql)
    {
        // Look for RETURNS clause - must handle variable whitespace
        // Pattern: RETURNS followed by either TABLE or @variable TABLE or a scalar type

        // Check for "RETURNS TABLE" (inline table-valued function)
        // DacFx classifies both inline and multi-statement table-valued functions as TableValuedFunction
        if (TryFindPatternWithFlexibleWhitespace(sql, "returns table") >= 0)
        {
            return SqlObjectType.TableValuedFunction;
        }

        // Check for "RETURNS @variable TABLE" (multi-statement table-valued function)
        // Pattern: RETURNS @something TABLE
        var returnsMatch = TryFindPatternWithFlexibleWhitespace(sql, "returns @");
        if (returnsMatch >= 0)
        {
            // Look for TABLE keyword after the variable name
            var afterReturns = sql.Substring(returnsMatch);
            if (TryFindPatternWithFlexibleWhitespace(afterReturns, "table") >= 0)
            {
                return SqlObjectType.TableValuedFunction;
            }
        }

        // Default to scalar function for other return types (INT, VARCHAR, etc.)
        return SqlObjectType.ScalarFunction;
    }

    /// <summary>
    /// Estimates the start position of a flexible whitespace pattern match.
    /// This is an approximation since the actual match length may vary due to whitespace.
    /// </summary>
    private static int EstimatePatternMatchLength(string sql, string pattern, int matchEnd)
    {
        // The minimum length is the pattern length (if all spaces match single spaces)
        // For a rough estimate, we scan backwards to find approximate start
        var tokenCount = pattern.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        var approximateLength = pattern.Length + (tokenCount - 1); // Allow for extra whitespace
        return Math.Min(approximateLength, matchEnd);
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

            // Trim leading blank lines from the batch before normalizing to match
            // the database side which doesn't include leading blank lines.
            var trimmedBatch = batch.TrimStart('\n', '\r');
            yield return (objectName, SqlScriptNormalizer.Normalize(trimmedBatch));
        }
    }

    /// <summary>
    /// Scans a table script for CREATE TRIGGER statements in subsequent batches
    /// (after the first GO) and returns each trigger as a separate object.
    /// This allows triggers embedded in table files to participate in comparison.
    /// </summary>
    private static IEnumerable<(string ObjectName, string Script)> ExtractTriggersFromScript(string normalizedSql)
    {
        if (string.IsNullOrWhiteSpace(normalizedSql))
        {
            yield break;
        }

        // Split into batches using the same GO semantics as other
        // normalization helpers, then scan all batches after the first for
        // CREATE TRIGGER statements.
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

            // Check if this batch contains a CREATE TRIGGER statement
            if (TryFindPatternWithFlexibleWhitespace(stripped, "create trigger") < 0 &&
                TryFindPatternWithFlexibleWhitespace(stripped, "create or alter trigger") < 0)
            {
                continue;
            }

            var triggerName = TryExtractTriggerName(stripped);
            if (string.IsNullOrEmpty(triggerName))
            {
                continue;
            }

            // Trim leading blank lines from the batch before normalizing to match
            // the database side which doesn't include leading blank lines.
            var trimmedBatch = batch.TrimStart('\n', '\r');
            yield return (triggerName, SqlScriptNormalizer.Normalize(trimmedBatch));
        }
    }

    private static string TryExtractIndexName(string sql)
    {
        // Extract the index name from a CREATE INDEX statement.
        // Pattern: CREATE [UNIQUE] [CLUSTERED|NONCLUSTERED] INDEX [name] ON ...
        // Uses flexible whitespace matching to find INDEX keyword, then parses the identifier.
        if (string.IsNullOrWhiteSpace(sql))
        {
            return string.Empty;
        }

        // Find the INDEX keyword using flexible whitespace matching
        var indexPos = TryFindPatternWithFlexibleWhitespace(sql, " index ");
        if (indexPos < 0)
        {
            // Try without leading space (e.g., at start of string)
            indexPos = TryFindPatternWithFlexibleWhitespace(sql, "index ");
            if (indexPos < 0)
            {
                return string.Empty;
            }
        }

        // Parse the identifier chain after INDEX to get the index name
        var identifiers = ParseIdentifierChain(sql, indexPos);
        if (identifiers.Count == 0)
        {
            return string.Empty;
        }

        // Return the first identifier (the index name, not the table name which comes after ON)
        return identifiers[0];
    }

    private static string TryExtractIndexTableName(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            return string.Empty;
        }

        // Find the ON keyword (case-insensitive) followed by the table name.
        // TryFindPatternWithFlexibleWhitespace returns the position immediately AFTER the match,
        // which is where the table name should begin.
        var afterOn = TryFindPatternWithFlexibleWhitespace(sql, " on ");
        if (afterOn < 0)
        {
            // Try without leading space (e.g., at start of string)
            afterOn = TryFindPatternWithFlexibleWhitespace(sql, "on ");
            if (afterOn < 0)
            {
                return string.Empty;
            }
        }

        // Parse the identifier chain after ON using the proper bracket-aware parser
        var parts = ParseIdentifierChain(sql, afterOn);
        if (parts.Count == 0)
        {
            return string.Empty;
        }

        // Return the last part (table name), which correctly preserves dots inside brackets
        // For [Schema].[Audit.TableName], parts will be ["Schema", "Audit.TableName"]
        return parts[^1];
    }

    /// <summary>
    /// Extracts the table name from a CREATE TABLE or ALTER TABLE statement.
    /// For names like [Schema].[Audit.TableName], returns "Audit.TableName"
    /// (the table name may contain dots which are preserved).
    /// </summary>
    internal static string TryExtractTableName(string sql)
    {
        return TryExtractObjectNameFromDdl(sql, new[] { "create table", "alter table" });
    }

    /// <summary>
    /// Extracts the view name from a CREATE VIEW or ALTER VIEW statement.
    /// </summary>
    internal static string TryExtractViewName(string sql)
    {
        return TryExtractObjectNameFromDdl(sql, new[] { "create or alter view", "create view", "alter view" });
    }

    /// <summary>
    /// Extracts the procedure name from a CREATE PROCEDURE or ALTER PROCEDURE statement.
    /// </summary>
    internal static string TryExtractProcedureName(string sql)
    {
        return TryExtractObjectNameFromDdl(sql, new[]
        {
            "create or alter procedure", "create or alter proc",
            "create procedure", "create proc",
            "alter procedure", "alter proc"
        });
    }

    /// <summary>
    /// Extracts the function name from a CREATE FUNCTION or ALTER FUNCTION statement.
    /// </summary>
    internal static string TryExtractFunctionName(string sql)
    {
        return TryExtractObjectNameFromDdl(sql, new[] { "create or alter function", "create function", "alter function" });
    }

    /// <summary>
    /// Extracts the trigger name from a CREATE TRIGGER or ALTER TRIGGER statement.
    /// </summary>
    internal static string TryExtractTriggerName(string sql)
    {
        return TryExtractObjectNameFromDdl(sql, new[] { "create or alter trigger", "create trigger", "alter trigger" });
    }

    /// <summary>
    /// Extracts the user name from a CREATE USER or ALTER USER statement.
    /// User names may contain dots (e.g., [tSQLt.TestClass]) which are preserved.
    /// </summary>
    internal static string TryExtractUserName(string sql)
    {
        return TryExtractObjectNameFromDdl(sql, new[] { "create or alter user", "create user", "alter user" });
    }

    /// <summary>
    /// Extracts the role name from a CREATE ROLE statement.
    /// Only CREATE ROLE and CREATE SERVER ROLE are supported; ALTER ROLE statements
    /// (which modify membership) are not role definitions and should not be extracted.
    /// Role names may contain dots which are preserved.
    /// </summary>
    internal static string TryExtractRoleName(string sql)
    {
        return TryExtractObjectNameFromDdl(sql, new[]
        {
            "create or alter role", "create role",
            "create server role"
        });
    }

    /// <summary>
    /// Generic helper that extracts the object name from a DDL statement given a set of patterns.
    /// Returns the last identifier in the chain (the object name, not the schema).
    /// Patterns should use single spaces; this method will convert them to regex patterns
    /// that match one or more whitespace characters.
    /// </summary>
    private static string TryExtractObjectNameFromDdl(string sql, string[] patterns)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            return string.Empty;
        }

        // Find the position after the first matching pattern using whitespace-tolerant matching.
        // SQL files may have variable whitespace (e.g., "CREATE   PROCEDURE" with multiple spaces).
        int matchEndIndex = -1;
        foreach (var pattern in patterns)
        {
            var match = TryFindPatternWithFlexibleWhitespace(sql, pattern);
            if (match >= 0)
            {
                matchEndIndex = match;
                break;
            }
        }

        if (matchEndIndex < 0)
        {
            return string.Empty;
        }

        // Parse the identifier chain (e.g., [Schema].[ObjectName] or schema.objectName)
        var identifiers = ParseIdentifierChain(sql, matchEndIndex);
        if (identifiers.Count == 0)
        {
            return string.Empty;
        }

        // Return the last identifier as the object name (may contain dots if it was bracketed)
        return identifiers[identifiers.Count - 1];
    }

    /// <summary>
    /// Finds a pattern in the SQL string where single spaces in the pattern can match
    /// one or more whitespace characters in the SQL. Returns the index immediately after
    /// the match, or -1 if not found.
    /// </summary>
    private static int TryFindPatternWithFlexibleWhitespace(string sql, string pattern)
    {
        // Convert the pattern to a regex where spaces match \s+
        var regexPattern = System.Text.RegularExpressions.Regex.Escape(pattern)
            .Replace(@"\ ", @"\s+");

        var regex = new System.Text.RegularExpressions.Regex(
            regexPattern,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        var match = regex.Match(sql);
        if (match.Success)
        {
            return match.Index + match.Length;
        }

        return -1;
    }

    /// <summary>
    /// Extracts the object name from a DDL statement based on the object type.
    /// Falls back to the provided fallback name if extraction fails.
    /// </summary>
    private static string TryExtractObjectNameForType(string sql, SqlObjectType objectType, string fallbackName)
    {
        var extracted = objectType switch
        {
            SqlObjectType.Table => TryExtractTableName(sql),
            SqlObjectType.View => TryExtractViewName(sql),
            SqlObjectType.StoredProcedure => TryExtractProcedureName(sql),
            SqlObjectType.ScalarFunction or SqlObjectType.TableValuedFunction or SqlObjectType.InlineTableValuedFunction => TryExtractFunctionName(sql),
            SqlObjectType.Trigger => TryExtractTriggerName(sql),
            SqlObjectType.Index => ExtractIndexObjectName(sql, fallbackName),
            SqlObjectType.User => TryExtractUserName(sql),
            SqlObjectType.Role => TryExtractRoleName(sql),
            _ => string.Empty
        };

        return !string.IsNullOrWhiteSpace(extracted) ? extracted : fallbackName;
    }

    /// <summary>
    /// Extracts the index object name in the format TableName.IndexName to match
    /// the database-side convention and avoid collisions when multiple tables
    /// share the same index name.
    /// </summary>
    private static string ExtractIndexObjectName(string sql, string fallbackName)
    {
        var indexName = TryExtractIndexName(sql);
        var tableName = TryExtractIndexTableName(sql);

        return !string.IsNullOrWhiteSpace(tableName) && !string.IsNullOrWhiteSpace(indexName)
            ? $"{tableName}.{indexName}"
            : fallbackName;
    }

    /// <summary>
    /// Keywords that indicate the end of an object name in a DDL statement.
    /// When parsing identifier chains, if we encounter one of these as an
    /// unquoted identifier, we know the object name has ended.
    /// </summary>
    private static readonly HashSet<string> ObjectNameTerminators = new(StringComparer.OrdinalIgnoreCase)
    {
        // Common DDL keywords
        "AS", "ON", "WITH", "FOR", "AFTER", "INSTEAD", "RETURNS", "BEGIN", "END",
        // User/Role specific keywords
        "WITHOUT", "FROM", "DEFAULT_SCHEMA", "AUTHORIZATION"
    };

    /// <summary>
    /// Parses a chain of SQL identifiers starting at the given position.
    /// Handles bracketed identifiers like [Schema].[Audit.TableName] where dots
    /// inside brackets are part of the identifier name, not separators.
    /// Returns a list of identifier parts (only the schema.objectname chain,
    /// stopping at keywords like AS, ON, etc.).
    /// </summary>
    private static List<string> ParseIdentifierChain(string sql, int startIndex)
    {
        var parts = new List<string>();
        var i = startIndex;

        // Skip leading whitespace
        while (i < sql.Length && char.IsWhiteSpace(sql[i]))
        {
            i++;
        }

        while (i < sql.Length)
        {
            // Skip whitespace between parts
            while (i < sql.Length && char.IsWhiteSpace(sql[i]))
            {
                i++;
            }

            if (i >= sql.Length)
            {
                break;
            }

            var ch = sql[i];

            // Stop if we hit something that's not part of an identifier chain
            if (ch == '(' || ch == ';' || ch == '\r' || ch == '\n')
            {
                break;
            }

            // Handle bracketed identifier [name]
            if (ch == '[')
            {
                i++; // skip opening bracket
                var sb = new StringBuilder();
                while (i < sql.Length && sql[i] != ']')
                {
                    sb.Append(sql[i]);
                    i++;
                }
                if (i < sql.Length && sql[i] == ']')
                {
                    i++; // skip closing bracket
                }
                parts.Add(sb.ToString());
            }
            // Handle quoted identifier "name"
            else if (ch == '"')
            {
                i++; // skip opening quote
                var sb = new StringBuilder();
                while (i < sql.Length && sql[i] != '"')
                {
                    sb.Append(sql[i]);
                    i++;
                }
                if (i < sql.Length && sql[i] == '"')
                {
                    i++; // skip closing quote
                }
                parts.Add(sb.ToString());
            }
            // Handle unquoted identifier
            else if (char.IsLetter(ch) || ch == '_')
            {
                var sb = new StringBuilder();
                while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] == '_'))
                {
                    sb.Append(sql[i]);
                    i++;
                }
                var token = sb.ToString();

                // Check if this is a keyword that terminates the object name
                if (ObjectNameTerminators.Contains(token))
                {
                    // Don't add the terminator to parts; we're done
                    break;
                }

                parts.Add(token);
            }
            // Handle dot separator between identifiers
            else if (ch == '.')
            {
                i++; // skip the dot and continue to next identifier
            }
            else
            {
                // Unknown character, stop parsing
                break;
            }
        }

        return parts;
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

