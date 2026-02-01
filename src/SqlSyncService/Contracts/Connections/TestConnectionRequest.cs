using System.ComponentModel.DataAnnotations;

namespace SqlSyncService.Contracts.Connections;

public sealed class TestConnectionRequest
{
    [Required]
    public string Server { get; set; } = string.Empty;

    [Required]
    public string Database { get; set; } = string.Empty;

    [Required]
    public string AuthType { get; set; } = string.Empty;

    public string? Username { get; set; }

    public string? Password { get; set; }

    public bool TrustServerCertificate { get; set; } = true;

    public int ConnectionTimeoutSeconds { get; set; } = 30;
}
