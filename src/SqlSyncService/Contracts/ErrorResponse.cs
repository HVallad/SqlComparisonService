namespace SqlSyncService.Contracts;

public sealed class ErrorResponse
{
    public ErrorDetail Error { get; set; } = new();
}
