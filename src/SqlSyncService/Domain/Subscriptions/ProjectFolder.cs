namespace SqlSyncService.Domain.Subscriptions;

public class ProjectFolder
{
    public string RootPath { get; set; } = string.Empty;
    public string[] IncludePatterns { get; set; } = new[] { "**/*.sql" };
    public string[] ExcludePatterns { get; set; } = new[] { "**/bin/**", "**/obj/**" };
    public FolderStructure Structure { get; set; } = FolderStructure.ByObjectType;
}

public enum FolderStructure
{
    Flat,
    ByObjectType,
    BySchema,
    BySchemaAndType
}

