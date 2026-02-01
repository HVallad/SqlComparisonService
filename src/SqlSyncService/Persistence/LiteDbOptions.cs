using System.ComponentModel.DataAnnotations;

namespace SqlSyncService.Persistence;

public class LiteDbOptions
{
    /// <summary>
    /// File path for the LiteDB database. Can be relative to the application base directory.
    /// </summary>
    [Required]
    public string DatabasePath { get; set; } = "Data/sqlsync.db";
}

