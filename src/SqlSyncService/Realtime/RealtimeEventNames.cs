namespace SqlSyncService.Realtime;

public static class RealtimeEventNames
{
    public const string FileChanged = "FileChanged";
    public const string DatabaseChanged = "DatabaseChanged";
    public const string SubscriptionHealthChanged = "SubscriptionHealthChanged";
    public const string ComparisonStarted = "ComparisonStarted";
    public const string ComparisonProgress = "ComparisonProgress";
    public const string ComparisonCompleted = "ComparisonCompleted";
    public const string ComparisonFailed = "ComparisonFailed";
    public const string DifferencesDetected = "DifferencesDetected";
    public const string SubscriptionStateChanged = "SubscriptionStateChanged";
    public const string SubscriptionCreated = "SubscriptionCreated";
    public const string SubscriptionDeleted = "SubscriptionDeleted";
    public const string ServiceShuttingDown = "ServiceShuttingDown";
    public const string ServiceReconnected = "ServiceReconnected";
}

