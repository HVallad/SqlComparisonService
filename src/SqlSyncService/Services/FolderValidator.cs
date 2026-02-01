using SqlSyncService.Contracts.Common;
using SqlSyncService.Contracts.Folders;

namespace SqlSyncService.Services;

public sealed class FolderValidator : IFolderValidator
{
    public Task<ValidateFolderResponse> ValidateFolderAsync(ValidateFolderRequest request, CancellationToken cancellationToken = default)
    {
        var path = request.Path;

        var response = new ValidateFolderResponse
        {
            Path = path,
            Exists = Directory.Exists(path)
        };

        if (!response.Exists)
        {
            response.Valid = false;
            return Task.FromResult(response);
        }

        response.IsWritable = IsDirectoryWritable(path);

        var sqlFiles = EnumerateSqlFiles(path).ToList();
        response.SqlFileCount = sqlFiles.Count;

        response.ObjectCounts = CalculateObjectCounts(sqlFiles);
        response.DetectedStructure = DetectStructure(path, sqlFiles);

        // Detailed parseErrors will be populated in later milestones when DacFX
        // and ScriptDom are integrated. For now this remains empty.
        response.Valid = response.Exists && response.IsWritable;

        return Task.FromResult(response);
    }

    private static bool IsDirectoryWritable(string path)
    {
        try
        {
            var testFile = System.IO.Path.Combine(path, $".write-test-{Guid.NewGuid():N}.tmp");
            using (File.Create(testFile, 1, FileOptions.DeleteOnClose))
            {
                // If we can create the file, assume the directory is writable.
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static IEnumerable<string> EnumerateSqlFiles(string rootPath)
    {
        if (!Directory.Exists(rootPath))
        {
            return Enumerable.Empty<string>();
        }

        return Directory.EnumerateFiles(rootPath, "*.sql", SearchOption.AllDirectories)
            .Where(path => !IsInIgnoredFolder(path));
    }

    private static bool IsInIgnoredFolder(string filePath)
    {
        var segments = filePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Any(segment =>
            segment.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals("obj", StringComparison.OrdinalIgnoreCase));
    }

    private static ObjectCounts CalculateObjectCounts(IEnumerable<string> sqlFiles)
    {
        var counts = new ObjectCounts();

        foreach (var file in sqlFiles)
        {
            var lower = file.ToLowerInvariant();

            if (lower.Contains("tables"))
            {
                counts.Tables++;
            }
            else if (lower.Contains("views"))
            {
                counts.Views++;
            }
            else if (lower.Contains("procedures") || lower.Contains("storedprocedures") || lower.Contains("procs"))
            {
                counts.StoredProcedures++;
            }
            else if (lower.Contains("functions"))
            {
                counts.Functions++;
            }
        }

        return counts;
    }

    private static string DetectStructure(string rootPath, IReadOnlyCollection<string> sqlFiles)
    {
        var rootDirectories = Directory.EnumerateDirectories(rootPath, "*", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToList();

        bool HasTypeDir(IEnumerable<string> names) => names.Any(name =>
            name.Equals("Tables", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Views", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("StoredProcedures", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Functions", StringComparison.OrdinalIgnoreCase));

        var hasSchemaDirs = rootDirectories.Any(name => name!.Contains('.', StringComparison.Ordinal));
        var hasTypeDirsAtRoot = HasTypeDir(rootDirectories!);

        if (hasSchemaDirs)
        {
            foreach (var schemaDir in rootDirectories.Where(name => name!.Contains('.', StringComparison.Ordinal)))
            {
                var fullPath = Path.Combine(rootPath, schemaDir!);
                var subDirs = Directory.EnumerateDirectories(fullPath, "*", SearchOption.TopDirectoryOnly)
                    .Select(Path.GetFileName)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .ToList();

                if (HasTypeDir(subDirs))
                {
                    return "by-schema-and-type";
                }
            }

            return "by-schema";
        }

        if (hasTypeDirsAtRoot)
        {
            return "by-type";
        }

        var hasSqlAtRoot = Directory.EnumerateFiles(rootPath, "*.sql", SearchOption.TopDirectoryOnly).Any();
        var hasSubdirectories = rootDirectories.Any();

        if (hasSqlAtRoot && !hasSubdirectories)
        {
            return "flat";
        }

        return "unknown";
    }
}
