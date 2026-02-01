using System.ComponentModel.DataAnnotations;

namespace SqlSyncService.Contracts.Folders;

public sealed class ValidateFolderRequest
{
    [Required]
    public string Path { get; set; } = string.Empty;

    public string[]? IncludePatterns { get; set; }

    public string[]? ExcludePatterns { get; set; }
}
