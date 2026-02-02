namespace SqlSyncService.Services;

public sealed class ComparisonInProgressException : Exception
{
    public ComparisonInProgressException()
        : base("A comparison is already in progress. Please try again later.")
    {
    }

    public ComparisonInProgressException(string message)
        : base(message)
    {
    }
}

