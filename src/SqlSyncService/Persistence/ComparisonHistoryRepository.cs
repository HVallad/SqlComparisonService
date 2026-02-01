using SqlSyncService.Domain.Comparisons;

namespace SqlSyncService.Persistence;

public class ComparisonHistoryRepository : IComparisonHistoryRepository
{
    private readonly LiteDbContext _context;

    public ComparisonHistoryRepository(LiteDbContext context)
    {
        _context = context;
    }

    public Task AddAsync(ComparisonResult result, CancellationToken cancellationToken = default)
    {
        if (result.Id == Guid.Empty)
        {
            result.Id = Guid.NewGuid();
        }

        _context.ComparisonHistory.Insert(result);
        return Task.CompletedTask;
    }

    public Task<ComparisonResult?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var value = _context.ComparisonHistory.FindById(id);
        return Task.FromResult<ComparisonResult?>(value);
    }

    public Task<IReadOnlyList<ComparisonResult>> GetBySubscriptionAsync(Guid subscriptionId, int? maxCount = null, CancellationToken cancellationToken = default)
    {
        var query = _context.ComparisonHistory
            .Find(r => r.SubscriptionId == subscriptionId)
            .OrderByDescending(r => r.ComparedAt);

        var list = maxCount.HasValue
            ? query.Take(maxCount.Value).ToList()
            : query.ToList();

        return Task.FromResult<IReadOnlyList<ComparisonResult>>(list);
    }
}

