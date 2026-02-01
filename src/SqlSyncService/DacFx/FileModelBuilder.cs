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

            var content = await File.ReadAllTextAsync(fullPath, cancellationToken).ConfigureAwait(false);
            var contentHash = ComputeSha256(Encoding.UTF8.GetBytes(content));

            var entry = new FileObjectEntry
            {
                FilePath = relativePath,
                ObjectName = Path.GetFileNameWithoutExtension(fullPath),
                ObjectType = InferObjectType(content),
                ContentHash = contentHash,
                LastModified = File.GetLastWriteTimeUtc(fullPath)
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
        if (sql.IndexOf("CREATE TABLE", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return SqlObjectType.Table;
        }

        if (sql.IndexOf("CREATE VIEW", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return SqlObjectType.View;
        }

        if (sql.IndexOf("CREATE PROCEDURE", StringComparison.OrdinalIgnoreCase) >= 0 ||
            sql.IndexOf("CREATE PROC", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return SqlObjectType.StoredProcedure;
        }

        if (sql.IndexOf("CREATE FUNCTION", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return SqlObjectType.ScalarFunction;
        }

        if (sql.IndexOf("CREATE TRIGGER", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return SqlObjectType.Trigger;
        }

        return SqlObjectType.Table;
    }

    private static string ComputeSha256(byte[] data)
    {
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(data);
        return Convert.ToHexString(hash);
    }
}

