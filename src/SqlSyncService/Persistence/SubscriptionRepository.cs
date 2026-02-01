using SqlSyncService.Domain.Subscriptions;

namespace SqlSyncService.Persistence;

public class SubscriptionRepository : ISubscriptionRepository
{
    private readonly LiteDbContext _context;

    public SubscriptionRepository(LiteDbContext context)
    {
        _context = context;
    }

    public Task<Subscription?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var result = _context.Subscriptions.FindById(id);
        return Task.FromResult<Subscription?>(result);
    }

    public Task<IReadOnlyList<Subscription>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var list = _context.Subscriptions.FindAll().ToList();
        return Task.FromResult<IReadOnlyList<Subscription>>(list);
    }

    public Task AddAsync(Subscription subscription, CancellationToken cancellationToken = default)
    {
        if (subscription.Id == Guid.Empty)
        {
            subscription.Id = Guid.NewGuid();
        }

        _context.Subscriptions.Insert(subscription);
        return Task.CompletedTask;
    }

    public Task UpdateAsync(Subscription subscription, CancellationToken cancellationToken = default)
    {
        _context.Subscriptions.Update(subscription);
        return Task.CompletedTask;
    }

    public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var removed = _context.Subscriptions.Delete(id);
        return Task.FromResult(removed);
    }
}

