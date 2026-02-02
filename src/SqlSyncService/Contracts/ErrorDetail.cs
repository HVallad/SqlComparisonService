namespace SqlSyncService.Contracts;

public sealed class ErrorDetail
{
    public string Code { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;

    public string? Details { get; set; }

    public string? Field { get; set; }

    public string? TraceId { get; set; }

    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
