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
	
		            var rawContent = await File.ReadAllTextAsync(fullPath, cancellationToken).ConfigureAwait(false);
		            var normalizedContent = SqlScriptNormalizer.Normalize(rawContent);
		            var contentHash = ComputeSha256(Encoding.UTF8.GetBytes(normalizedContent));
		
		            var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(fullPath);
		            var objectName = ExtractObjectName(fileNameWithoutExtension);
		
		            var entry = new FileObjectEntry
		            {
		                FilePath = relativePath,
		                ObjectName = objectName,
		                ObjectType = InferObjectType(normalizedContent),
		                ContentHash = contentHash,
		                LastModified = File.GetLastWriteTimeUtc(fullPath),
		                Content = normalizedContent
		            };

            cache.FileEntries[relativePath] = entry;
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

