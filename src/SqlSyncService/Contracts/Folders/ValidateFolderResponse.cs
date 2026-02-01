using SqlSyncService.Contracts.Common;

namespace SqlSyncService.Contracts.Folders;

public sealed class ValidateFolderResponse
{
    public bool Valid { get; set; }

    public string Path { get; set; } = string.Empty;

    public bool Exists { get; set; }

    public bool IsWritable { get; set; }

    public string DetectedStructure { get; set; } = "unknown";

    public int SqlFileCount { get; set; }

    public ObjectCounts ObjectCounts { get; set; } = new();

    public List<FolderParseError> ParseErrors { get; set; } = new();
}
