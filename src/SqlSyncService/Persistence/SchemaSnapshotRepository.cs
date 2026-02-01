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
}

