namespace SqlSyncService.Contracts;

public static class ErrorCodes
{
    public const string ValidationError = "VALIDATION_ERROR";
    public const string InternalError = "INTERNAL_ERROR";
    public const string ConnectionFailed = "CONNECTION_FAILED";
    public const string NotFound = "NOT_FOUND";
		    public const string Conflict = "CONFLICT";
		    public const string ComparisonInProgress = "COMPARISON_IN_PROGRESS";
}
