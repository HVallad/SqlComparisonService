using System.ComponentModel.DataAnnotations;

namespace SqlSyncService.Configuration;

public class ServiceConfiguration
{
    [Required]
    public ServerSettings Server { get; set; } = new();

    [Required]
    public MonitoringSettings Monitoring { get; set; } = new();

    [Required]
    public CacheSettings Cache { get; set; } = new();

    [Required]
    public LoggingSettings Logging { get; set; } = new();
}

public class ServerSettings
{
    [Range(1024, 65535)]
    public int HttpPort { get; set; } = 5050;

    [Range(1024, 65535)]
    public int WebSocketPort { get; set; } = 5051;

    public bool EnableHttps { get; set; } = false;

    public string? CertificatePath { get; set; }
}

public class MonitoringSettings
{
    [Required]
    public TimeSpan DatabasePollInterval { get; set; } = TimeSpan.FromSeconds(30);

    [Required]
    public TimeSpan FileSystemDebounce { get; set; } = TimeSpan.FromMilliseconds(500);

    [Required]
    public TimeSpan FullReconciliationInterval { get; set; } = TimeSpan.FromMinutes(5);

    [Range(1, 32)]
    public int MaxConcurrentComparisons { get; set; } = 2;
}

public class CacheSettings
{
    [Required]
    public string CacheDirectory { get; set; } = "./cache";

    [Required]
    public TimeSpan SnapshotRetention { get; set; } = TimeSpan.FromDays(7);

    [Range(1, 100)]
    public int MaxCachedSnapshots { get; set; } = 10;
}

public class LoggingSettings
{
    [Required]
    public string MinimumLevel { get; set; } = "Information";

    public bool EnableConsole { get; set; } = true;

    public bool EnableFile { get; set; } = false;

    public string? FilePath { get; set; }
}

