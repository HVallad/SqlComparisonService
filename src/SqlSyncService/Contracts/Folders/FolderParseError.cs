namespace SqlSyncService.Contracts.Folders;

public sealed class FolderParseError
{
    public string File { get; set; } = string.Empty;

    public int Line { get; set; }

    public string Message { get; set; } = string.Empty;
}
