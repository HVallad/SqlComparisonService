using LiteDB;
using SqlSyncService.Domain.Caching;
using SqlSyncService.Domain.Changes;
using SqlSyncService.Domain.Comparisons;
using SqlSyncService.Domain.Subscriptions;

namespace SqlSyncService.Persistence;

public class LiteDbContext
{
    private readonly ILiteDatabase _database;

    public LiteDbContext(ILiteDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        ConfigureCollections();
    }

    public ILiteCollection<Subscription> Subscriptions => _database.GetCollection<Subscription>("subscriptions");
    public ILiteCollection<SchemaSnapshot> SchemaSnapshots => _database.GetCollection<SchemaSnapshot>("schema_snapshots");
    public ILiteCollection<ComparisonResult> ComparisonHistory => _database.GetCollection<ComparisonResult>("comparison_history");
    public ILiteCollection<DetectedChange> PendingChanges => _database.GetCollection<DetectedChange>("pending_changes");

    private void ConfigureCollections()
    {
        // Subscriptions
			// LiteDB automatically creates a unique index on the Id (primary key) field,
			// so we only need an additional non-unique index on Name for lookup scenarios.
			Subscriptions.EnsureIndex(nameof(Subscription.Name));

        // Schema snapshots
		SchemaSnapshots.EnsureIndex(nameof(SchemaSnapshot.SubscriptionId));
		SchemaSnapshots.EnsureIndex(nameof(SchemaSnapshot.CapturedAt));

        // Comparison history
		ComparisonHistory.EnsureIndex(nameof(ComparisonResult.SubscriptionId));
		ComparisonHistory.EnsureIndex(nameof(ComparisonResult.ComparedAt));

        // Pending changes
		PendingChanges.EnsureIndex(nameof(DetectedChange.SubscriptionId));
		PendingChanges.EnsureIndex(nameof(DetectedChange.IsProcessed));
    }
}

