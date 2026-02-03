using SqlSyncService.Domain.Caching;

namespace SqlSyncService.Persistence;

public class SchemaSnapshotRepository : ISchemaSnapshotRepository
{
    private readonly LiteDbContext _context;

    public SchemaSnapshotRepository(LiteDbContext context)
    {
        _context = context;
    }

    public Task AddAsync(SchemaSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        if (snapshot.Id == Guid.Empty)
        {
            snapshot.Id = Guid.NewGuid();
        }

        _context.SchemaSnapshots.Insert(snapshot);
        return Task.CompletedTask;
    }

    public Task<SchemaSnapshot?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var result = _context.SchemaSnapshots.FindById(id);
        return Task.FromResult<SchemaSnapshot?>(result);
    }

    public Task<IReadOnlyList<SchemaSnapshot>> GetBySubscriptionAsync(Guid subscriptionId, CancellationToken cancellationToken = default)
    {
        var results = _context.SchemaSnapshots
            .Find(s => s.SubscriptionId == subscriptionId)
            .OrderBy(s => s.CapturedAt)
            .ToList();

        return Task.FromResult<IReadOnlyList<SchemaSnapshot>>(results);
    }

    public Task<SchemaSnapshot?> GetLatestForSubscriptionAsync(Guid subscriptionId, CancellationToken cancellationToken = default)
    {
        var result = _context.SchemaSnapshots
            .Find(s => s.SubscriptionId == subscriptionId)
            .OrderByDescending(s => s.CapturedAt)
            .FirstOrDefault();

        return Task.FromResult<SchemaSnapshot?>(result);
    }

    public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var deleted = _context.SchemaSnapshots.Delete(id);
        return Task.FromResult(deleted);
    }

    public Task DeleteForSubscriptionAsync(Guid subscriptionId, CancellationToken cancellationToken = default)
    {
        _context.SchemaSnapshots.DeleteMany(s => s.SubscriptionId == subscriptionId);
        return Task.CompletedTask;
    }

    public Task<int> DeleteOlderThanAsync(DateTime cutoffDate, CancellationToken cancellationToken = default)
    {
        var deleted = _context.SchemaSnapshots.DeleteMany(s => s.CapturedAt < cutoffDate);
        return Task.FromResult(deleted);
    }

    public Task<int> DeleteExcessForSubscriptionAsync(Guid subscriptionId, int maxCount, CancellationToken cancellationToken = default)
    {
        // Get all snapshots for the subscription ordered by date (newest first)
        var snapshots = _context.SchemaSnapshots
            .Find(s => s.SubscriptionId == subscriptionId)
            .OrderByDescending(s => s.CapturedAt)
            .ToList();

        if (snapshots.Count <= maxCount)
        {
            return Task.FromResult(0);
        }

        // Delete excess snapshots (keeping the most recent ones)
        var toDelete = snapshots.Skip(maxCount).Select(s => s.Id).ToList();
        var deletedCount = 0;

        foreach (var id in toDelete)
        {
            if (_context.SchemaSnapshots.Delete(id))
            {
                deletedCount++;
            }
        }

        return Task.FromResult(deletedCount);
    }
}
