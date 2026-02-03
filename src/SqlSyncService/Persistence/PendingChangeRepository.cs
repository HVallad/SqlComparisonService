using SqlSyncService.Domain.Changes;

namespace SqlSyncService.Persistence;

public class PendingChangeRepository : IPendingChangeRepository
{
    private readonly LiteDbContext _context;

    public PendingChangeRepository(LiteDbContext context)
    {
        _context = context;
    }

    public Task AddAsync(DetectedChange change, CancellationToken cancellationToken = default)
    {
        if (change.Id == Guid.Empty)
        {
            change.Id = Guid.NewGuid();
        }

        _context.PendingChanges.Insert(change);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<DetectedChange>> GetPendingForSubscriptionAsync(Guid subscriptionId, CancellationToken cancellationToken = default)
    {
        var items = _context.PendingChanges
            .Find(c => c.SubscriptionId == subscriptionId && !c.IsProcessed)
            .OrderBy(c => c.DetectedAt)
            .ToList();

        return Task.FromResult<IReadOnlyList<DetectedChange>>(items);
    }

    public Task MarkAsProcessedAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var change = _context.PendingChanges.FindById(id);
        if (change is null)
        {
            return Task.CompletedTask;
        }

        change.IsProcessed = true;
        _context.PendingChanges.Update(change);

        return Task.CompletedTask;
    }

    public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var deleted = _context.PendingChanges.Delete(id);
        return Task.FromResult(deleted);
    }

    public Task<int> DeleteProcessedOlderThanAsync(DateTime cutoffDate, CancellationToken cancellationToken = default)
    {
        var deleted = _context.PendingChanges.DeleteMany(c => c.IsProcessed && c.DetectedAt < cutoffDate);
        return Task.FromResult(deleted);
    }
}
